using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Forms.Integration;
using System.Windows.Threading;

namespace ClaudeUsageIndicator;

public partial class App : System.Windows.Application
{
    private Mutex? _mutex;
    private ConfigStore _store = null!;
    private AppConfig _config = null!;
    private AppState _state = null!;
    private UsageService _service = null!;
    private MainWindow _window = null!;
    private TrayIcon? _tray;
    private DispatcherTimer? _pollTimer;
    private DispatcherTimer? _tickTimer;
    private bool _hiddenByFullscreen;
    private bool _polling;
    /// <summary>The fetch the backoff is measured against: the last one that brought a visible change.</summary>
    private UsageSnapshot? _baseline;
    private int _pollSeconds;
    /// <summary>The poll interval sits at its maximum and the last fetch brought nothing new.</summary>
    private bool _backedOff;
    private SessionRegistry? _sessions;
    private int _ticksSinceSessionScan;
    private readonly Chime _chime = new();
    /// <summary>Per session PID: was it waiting the last time we looked? Drives the chime.</summary>
    private readonly Dictionary<int, bool> _wasWaiting = new();
    /// <summary>Sessions that just went waiting, by PID, and when: they chime once that has held for a moment.</summary>
    private readonly Dictionary<int, DateTimeOffset> _pendingChime = new();
    private DateTimeOffset _lastChime = DateTimeOffset.MinValue;
    /// <summary>Double-click override of the compact layout, and what the automatic verdict was when it was set.</summary>
    private bool? _manualCompact;
    private bool _autoCompactAtOverride;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        DispatcherUnhandledException += (_, args) =>
        {
            Log.Error("Unhandled exception: " + args.Exception);
            args.Handled = true;
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            Log.Error("Unobserved task exception: " + args.Exception);
            args.SetObserved();
        };

        try
        {
            _store = new ConfigStore();
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show($"Claude Usage Indicator cannot create its settings folder:\n{ex.Message}",
                "Claude Usage Indicator", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }
        Log.Init(_store.LogPath);
        if (_store.MigratedFrom is { } legacy) Log.Info($"Moved settings from {legacy}");

        _config = _store.LoadConfig();
        _state = _store.LoadState();
        var demo = e.Args.Contains("--demo", StringComparer.OrdinalIgnoreCase);
        if (demo) ApplyDemoOverrides();
        _service = new UsageService(_config) { Demo = demo };

        if (e.Args.Contains("--dump", StringComparer.OrdinalIgnoreCase))
        {
            _ = RunDumpAsync();
            return;
        }
        if (e.Args.Contains("--chime", StringComparer.OrdinalIgnoreCase))
        {
            RunChime();
            return;
        }

        // A demo runs beside the real overlay rather than being turned away by it.
        _mutex = new Mutex(true, demo ? @"Local\ClaudeUsageIndicator.Demo" : @"Local\ClaudeUsageIndicator.SingleInstance", out var createdNew);
        if (!createdNew)
        {
            Log.Info("Another instance is already running; exiting.");
            Shutdown();
            return;
        }

        Log.Info($"Starting v{typeof(App).Assembly.GetName().Version}; config: {_store.ConfigPath}");

        // Lets WinForms context menus (used by the tray icon) behave inside a WPF message loop.
        WindowsFormsHost.EnableWindowsFormsInterop();
        System.Windows.Forms.Application.EnableVisualStyles();

        _window = new MainWindow(_config, _state, _store);
        _window.ContextMenuRequested += () => _tray?.ShowMenu();
        _window.CompactToggleRequested += ToggleCompact;

        _tray = new TrayIcon();
        _tray.ToggleVisibility += ToggleVisibility;
        _tray.RefreshRequested += () => { _baseline = null; _ = PollAsync(); };
        _tray.OpenConfigRequested += () => OpenFile(_store.ConfigPath);
        _tray.OpenLogRequested += () => OpenFile(_store.LogPath);
        _tray.ExitRequested += ExitApp;
        _tray.RunAtStartupChanged += StartupRegistration.Set;
        _tray.OpacitySelected += SetOpacity;
        _tray.ChimeChanged += SetChimeEnabled;
        _tray.SetOpacity(_config.Opacity);
        _tray.SetChime(_config.SoundOnWaiting);
        if (!demo) StartupRegistration.MigrateLegacy();
        _tray.SetRunAtStartup(StartupRegistration.IsEnabled);
        _tray.SetVisibleState(!_state.Hidden);
        _tray.Apply(_service, _config, null);

        if (!demo)
        {
            // A live reload would undo the demo overrides.
            _store.ConfigChanged += OnConfigChanged;
            _store.WatchConfig(Dispatcher);
        }

        _sessions = demo ? new SessionRegistry(DemoData.Sessions(DateTimeOffset.UtcNow)) : new SessionRegistry();
        _sessions.Changed += () => ScanSessions(force: true);
        _sessions.Watch(Dispatcher);
        ScanSessions(force: true);

        if (!_state.Hidden) _window.ShowNoActivate();

        _pollSeconds = _config.PollIntervalSeconds;
        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(_pollSeconds) };
        _pollTimer.Tick += (_, _) => _ = PollAsync();
        _pollTimer.Start();

        _tickTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _tickTimer.Tick += (_, _) => Tick();
        _tickTimer.Start();

        _ = PollAsync();

        if (demo && ArgAfter(e.Args, "--shot") is { } shotPath)
        {
            // Once the first render has settled, write the image and quit.
            Dispatcher.BeginInvoke(DispatcherPriority.ApplicationIdle, () => SaveShot(Path.GetFullPath(shotPath)));
        }
    }

    private static string? ArgAfter(string[] args, string flag)
    {
        var i = Array.FindIndex(args, a => a.Equals(flag, StringComparison.OrdinalIgnoreCase));
        return i >= 0 && i + 1 < args.Length ? args[i + 1] : null;
    }

    private void SaveShot(string path)
    {
        try
        {
            _window.SaveImage(path, 2);
            Log.Info($"Demo screenshot written to {path}");
        }
        catch (Exception ex)
        {
            Log.Error("Demo screenshot failed: " + ex);
        }
        ExitApp();
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _sessions?.Dispose();
        _store?.Dispose();
        _tray?.Dispose();
        _chime.Dispose();
        _mutex?.Dispose();
        base.OnExit(e);
    }

    private async Task PollAsync()
    {
        if (_polling) return;
        _polling = true;
        try
        {
            await _service.PollAsync(CancellationToken.None);
        }
        finally
        {
            _polling = false;
        }
        _window.Apply(_service);
        _tray?.Apply(_service, _config, _sessions?.Sessions);
        ScheduleNextPoll();
    }

    /// <summary>
    /// Back off while nothing changes: double the interval up to the configured maximum, and
    /// return to the base interval as soon as the numbers move (or a fetch fails).
    /// </summary>
    private void ScheduleNextPoll()
    {
        if (_pollTimer is null) return;
        var snapshot = _service.Error is null ? _service.Snapshot : null;
        var unchanged = snapshot is not null && _baseline is not null && snapshot.IsSteadyAgainst(_baseline);
        var next = unchanged
            ? Math.Min(_pollSeconds * 2, _config.MaxPollIntervalSeconds)
            : _config.PollIntervalSeconds;
        if (!unchanged)
        {
            if (snapshot is not null && _baseline is not null && _pollSeconds > _config.PollIntervalSeconds)
            {
                Log.Info($"Usage changed, backoff reset: {_baseline.ContentKey} -> {snapshot.ContentKey}");
            }
            _baseline = snapshot;
        }

        if (next != _pollSeconds)
        {
            Log.Info($"Poll interval {_pollSeconds}s -> {next}s");
            _pollSeconds = next;
        }
        _backedOff = unchanged && _pollSeconds >= _config.MaxPollIntervalSeconds;
        UpdateCompact();
        // Restart so the new interval counts from now, not from the last tick.
        _pollTimer.Stop();
        _pollTimer.Interval = TimeSpan.FromSeconds(_pollSeconds);
        _pollTimer.Start();
    }

    /// <summary>
    /// Re-read the session registry. File events cover status changes; the periodic scan catches
    /// processes that died without cleaning up and sessions crossing the idle cutoff.
    /// </summary>
    private void ScanSessions(bool force)
    {
        if (_sessions is null) return;
        // Always scan, even with the list off: the chime and compact mode both need to know.
        var changed = _sessions.Scan(_config.SessionsIncludeBackground, TimeSpan.FromHours(_config.SessionsHideIdleAfterHours));
        if (changed) NoteTransitions(_sessions.Sessions);
        if (changed || force)
        {
            _window.ApplySessions(_sessions.Sessions);
            _tray?.Apply(_service, _config, _sessions.Sessions);
        }
        UpdateCompact();
    }

    /// <summary>
    /// Compact (dial) while nothing is going on: no session in the list (same filtering as the
    /// display, so a recently finished session still counts as activity) and the poll interval
    /// fully backed off because usage has stopped changing, which also covers sessions on other
    /// machines. Errors and the pre-first-fetch state always show the full layout.
    /// </summary>
    private void UpdateCompact()
    {
        var auto = AutoCompact();
        // A double-click override lasts until the automatic verdict next changes, i.e. until
        // activity genuinely starts or stops; from then on the layout follows that again.
        if (_manualCompact is not null && auto != _autoCompactAtOverride)
        {
            Log.Info("Compact override cleared: activity changed.");
            _manualCompact = null;
        }
        _window.SetCompact(_manualCompact ?? auto);
    }

    private bool AutoCompact() => _config.CompactMode switch
    {
        "always" => true,
        "never" => false,
        _ => _backedOff && _service.Error is null && _service.Snapshot is not null && (_sessions?.Sessions.Count ?? 0) == 0,
    };

    /// <summary>Double-click on the overlay: shrink to the dial, or expand it back.</summary>
    private void ToggleCompact()
    {
        var auto = AutoCompact();
        var target = !_window.IsCompact;
        _manualCompact = target == auto ? null : target;
        _autoCompactAtOverride = auto;
        Log.Info($"Compact override: {(target ? "compact" : "expanded")}{(_manualCompact is null ? " (matches auto)" : "")}");
        _window.SetCompact(target);
    }

    /// <summary>
    /// Note sessions we already knew as working that are now waiting. They do not chime yet:
    /// Claude Code flips a session to "idle" for a moment when a background subagent finishes
    /// and its result re-invokes the parent, so a candidate only chimes once it has stayed
    /// waiting for <see cref="AppConfig.SoundConfirmSeconds"/> (see <see cref="ConfirmPendingChimes"/>).
    /// Sessions seen for the first time (startup, a fresh `claude` that has not been given a
    /// prompt) never chime.
    /// </summary>
    private void NoteTransitions(IReadOnlyList<SessionInfo> sessions)
    {
        var now = DateTimeOffset.UtcNow;
        var seen = new HashSet<int>();
        foreach (var s in sessions)
        {
            seen.Add(s.Pid);
            if (_wasWaiting.TryGetValue(s.Pid, out var was))
            {
                if (!was && s.Waiting)
                {
                    _pendingChime[s.Pid] = now;
                }
                else if (!s.Waiting && _pendingChime.Remove(s.Pid))
                {
                    Log.Info($"Session resumed before chime: {s.Label("folder-title")}");
                }
            }
            _wasWaiting[s.Pid] = s.Waiting;
        }
        var gone = new List<int>();
        foreach (var pid in _wasWaiting.Keys) if (!seen.Contains(pid)) gone.Add(pid);
        foreach (var pid in gone)
        {
            _wasWaiting.Remove(pid);
            _pendingChime.Remove(pid);
        }
    }

    /// <summary>
    /// Called every second. Chime for candidates that have been waiting long enough, provided
    /// the registry still says so right now; several confirming together chime once.
    /// </summary>
    private void ConfirmPendingChimes()
    {
        if (_pendingChime.Count == 0 || _sessions is null) return;
        var now = DateTimeOffset.UtcNow;
        var due = new List<(int Pid, DateTimeOffset Since)>();
        foreach (var (pid, since) in _pendingChime)
        {
            if (now - since >= TimeSpan.FromSeconds(_config.SoundConfirmSeconds)) due.Add((pid, since));
        }
        if (due.Count == 0) return;

        // Re-read the registry so a session that went busy again between scans does not chime.
        ScanSessions(force: false);
        var finished = false;
        foreach (var (pid, since) in due)
        {
            // Dropped or restarted by the rescan: it was seen working again in the meantime.
            if (!_pendingChime.TryGetValue(pid, out var current) || current != since) continue;
            _pendingChime.Remove(pid);
            SessionInfo? s = null;
            foreach (var candidate in _sessions.Sessions) if (candidate.Pid == pid) { s = candidate; break; }
            if (s is null || !s.Waiting) continue;
            finished = true;
            Log.Info($"Session finished: {s.Label("folder-title")}");
        }

        if (!finished || !_config.SoundOnWaiting) return;
        if (now - _lastChime < TimeSpan.FromSeconds(2)) return;
        _lastChime = now;
        PlayChime();
    }

    private void PlayChime()
    {
        try
        {
            _chime.Play(_config.SoundVolume);
        }
        catch (Exception ex)
        {
            Log.Error($"Chime failed: {ex.Message}");
        }
    }

    /// <summary>Tray toggle: persist into config.json and, when turning it on, play it once as a preview.</summary>
    private void SetChimeEnabled(bool enabled)
    {
        _config.SoundOnWaiting = enabled;
        _store.SetConfigValue("soundOnWaiting", enabled ? "true" : "false");
        if (enabled) PlayChime();
        ScanSessions(force: false); // establishes the waiting/working baseline right away
    }

    private void Tick()
    {
        var now = DateTimeOffset.UtcNow;
        _window.UpdateClock(now);

        if (++_ticksSinceSessionScan >= 15)
        {
            _ticksSinceSessionScan = 0;
            ScanSessions(force: false);
        }
        ConfirmPendingChimes();

        if (_config.HideOverFullscreen && !_state.Hidden)
        {
            var fullscreen = FullscreenDetector.IsFullscreenAppActive(_window.Handle);
            if (fullscreen && !_hiddenByFullscreen)
            {
                _hiddenByFullscreen = true;
                _window.Hide();
                Log.Info("Fullscreen app detected; hiding overlay.");
            }
            else if (!fullscreen && _hiddenByFullscreen)
            {
                _hiddenByFullscreen = false;
                _window.ShowNoActivate();
                Log.Info("Fullscreen app gone; showing overlay.");
            }
        }
        else if (_hiddenByFullscreen)
        {
            _hiddenByFullscreen = false;
            if (!_state.Hidden) _window.ShowNoActivate();
        }

        if (_window.IsVisible) _window.AssertTopmost();
    }

    /// <summary>Apply an opacity preset now and persist it into config.json (the watcher reload is a no-op then).</summary>
    private void SetOpacity(double opacity)
    {
        _config.Opacity = opacity;
        _window.ApplyConfig(_config);
        _tray?.SetOpacity(opacity);
        _store.SetConfigValue("opacity", opacity.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture));
    }

    private void ToggleVisibility()
    {
        _state.Hidden = !_state.Hidden;
        _store.SaveState(_state);
        _tray?.SetVisibleState(!_state.Hidden);
        if (_state.Hidden)
        {
            _window.Hide();
        }
        else if (!_hiddenByFullscreen)
        {
            _window.ShowNoActivate();
        }
    }

    private async void OnConfigChanged()
    {
        // Editors often write in several steps; retry briefly and keep the old config on failure.
        AppConfig? fresh = null;
        for (var attempt = 0; attempt < 4 && fresh is null; attempt++)
        {
            if (attempt > 0) await Task.Delay(250);
            fresh = _store.TryLoadConfig();
        }
        if (fresh is null)
        {
            Log.Error("Config change ignored: file unreadable or invalid; keeping previous settings.");
            return;
        }

        _config = fresh;
        _service.Config = fresh;
        _window.ApplyConfig(fresh);
        if (_pollTimer is not null)
        {
            _pollSeconds = Math.Clamp(_pollSeconds, fresh.PollIntervalSeconds, fresh.MaxPollIntervalSeconds);
            _pollTimer.Interval = TimeSpan.FromSeconds(_pollSeconds);
        }
        ScanSessions(force: true);
        _tray?.SetOpacity(fresh.Opacity);
        _tray?.SetChime(fresh.SoundOnWaiting);
        Log.Info("Config reloaded.");
    }

    private static void OpenFile(string path)
    {
        try
        {
            Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            Log.Error($"Could not open {path}: {ex.Message}");
        }
    }

    private void ExitApp()
    {
        Log.Info("Exit requested.");
        _tickTimer?.Stop();
        _pollTimer?.Stop();
        _window.Close();
        Shutdown();
    }

    /// <summary>
    /// --demo: fake numbers and sessions (see <see cref="DemoData"/>) for screenshots. Appearance
    /// comes from the user's config, layout is pinned to the full list, and nothing is written
    /// back: the window opens at the default spot and moving it or using the tray is not saved.
    /// </summary>
    private void ApplyDemoOverrides()
    {
        _store.ReadOnly = true;
        _state = new AppState();
        _config.CompactMode = "never";
        _config.SessionsMode = "expanded";
        _config.SessionsLabel = "folder-title";
        _config.SessionsIncludeBackground = false;
        _config.SoundOnWaiting = false;
        _config.HideOverFullscreen = false;
        Log.Info("Demo mode: showing canned usage and sessions.");
    }

    /// <summary>--chime: write chime.wav next to the config, play the chime once, and exit.</summary>
    private void RunChime()
    {
        try
        {
            var wavPath = Path.Combine(_store.Dir, "chime.wav");
            File.WriteAllBytes(wavPath, Chime.RenderWav(_config.SoundVolume));
            Log.Info($"Wrote {wavPath}; playing at volume {_config.SoundVolume:0.##}");
            _chime.PlayAndWait(_config.SoundVolume);
        }
        catch (Exception ex)
        {
            Log.Error("Chime failed: " + ex);
        }
        Shutdown();
    }

    /// <summary>--dump: fetch the raw usage JSON once, write it next to the config, and exit.</summary>
    private async Task RunDumpAsync()
    {
        try
        {
            var json = await _service.FetchRawAsync(CancellationToken.None);
            File.WriteAllText(_store.DumpPath, json);
            Log.Info($"Wrote usage dump to {_store.DumpPath}");
        }
        catch (Exception ex)
        {
            File.WriteAllText(_store.DumpPath, "ERROR: " + ex);
            Log.Error("Dump failed: " + ex);
        }
        Shutdown();
    }
}
