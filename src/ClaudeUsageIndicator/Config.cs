using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Windows.Threading;

namespace ClaudeUsageIndicator;

/// <summary>User-editable settings. Lives in config.json and is never rewritten by the app.</summary>
public sealed class AppConfig
{
    public double Opacity { get; set; } = 0.9;
    public double Scale { get; set; } = 1.0;
    public int PollIntervalSeconds { get; set; } = 120;
    public int MaxPollIntervalSeconds { get; set; } = 600;
    public bool HideOverFullscreen { get; set; } = true;
    public bool AlwaysOnTop { get; set; } = true;
    public bool LockPosition { get; set; } = false;

    /// <summary>"auto" (dial while idle), "always" or "never".</summary>
    public string CompactMode { get; set; } = "auto";
    public bool ShowSession { get; set; } = true;
    public bool ShowWeekly { get; set; } = true;
    public bool ShowFable { get; set; } = true;
    public bool ShowResetTimes { get; set; } = true;
    public bool ShowTimeMarker { get; set; } = true;
    public bool Use24HourClock { get; set; } = true;
    public string BackgroundColor { get; set; } = "#E6141820";
    public string TextColor { get; set; } = "#FFF2F4F8";
    public string? CredentialsPath { get; set; }
    public bool AllowTokenRefresh { get; set; } = true;
    public string? FableLimitKey { get; set; }

    /// <summary>"off", "collapsed" (one summary line) or "expanded" (one line per session).</summary>
    public string SessionsMode { get; set; } = "expanded";
    public double SessionsHideIdleAfterHours { get; set; } = 3;

    /// <summary>"folder-title", "folder", "title", "prompt" or "folder-prompt".</summary>
    public string SessionsLabel { get; set; } = "folder-title";
    public bool SessionsIncludeBackground { get; set; } = false;

    /// <summary>Play <see cref="Chime"/> when a listed session goes from working to waiting.</summary>
    public bool SoundOnWaiting { get; set; } = false;
    public double SoundVolume { get; set; } = 0.5;
    public int SoundConfirmSeconds { get; set; } = 5;
}

/// <summary>App-owned state (window position, hidden flag). Lives in state.json.</summary>
public sealed class AppState
{
    public double? X { get; set; }
    public double? Y { get; set; }
    public bool Hidden { get; set; }
}

public sealed class ConfigStore : IDisposable
{
    private const string DefaultConfigText = """
        // Claude Usage Indicator configuration.
        // Edit by hand and save; the running app picks changes up immediately.
        // Comments and trailing commas are allowed. Window position and the hidden/visible
        // flag live in state.json next to this file. The app only edits this file when you pick
        // a preset from the tray's Opacity menu or toggle the chime there, and then only that value.
        {
          // Overlay opacity, 0.1 - 1.0.
          "opacity": 0.9,

          // Size multiplier for the whole overlay (1.0 = default, 1.5 = 150%).
          "scale": 1.0,

          // How often to ask Anthropic for fresh usage numbers (minimum 10). While the numbers
          // keep coming back unchanged the interval doubles each time, up to maxPollIntervalSeconds,
          // and snaps back to pollIntervalSeconds as soon as anything moves.
          "pollIntervalSeconds": 120,
          "maxPollIntervalSeconds": 600,

          // Hide the overlay while a fullscreen app (game, fullscreen video, presentation) is in
          // front on the overlay's monitor.
          "hideOverFullscreen": true,

          // Keep the overlay above normal windows.
          "alwaysOnTop": true,

          // Prevent accidental dragging.
          "lockPosition": false,

          // Shrink to a small dial (session ring outside, weekly ring inside, like the tray icon)
          // while no Claude Code session is listed and usage has stopped changing, i.e. the poll
          // interval has backed off to maxPollIntervalSeconds. Hover the dial for the numbers. It
          // expands again as soon as a session starts or usage moves. "auto", "always" or "never".
          // Double-click the overlay to switch layouts by hand.
          "compactMode": "auto",

          // Which rows to show.
          "showSession": true,
          "showWeekly": true,
          "showFable": true,
          "showResetTimes": true,

          // Tick on each bar at "how far through the time window we are" (5h session, 7d weekly).
          // Fill ahead of the tick means usage is running faster than the window.
          "showTimeMarker": true,

          // 24-hour (true) or 12-hour (false) clock for reset times.
          "use24HourClock": true,

          // Colours as #AARRGGBB (alpha first) or #RRGGBB.
          "backgroundColor": "#E6141820",
          "textColor": "#FFF2F4F8",

          // Path to Claude Code's credentials file. null = auto-detect
          // (%CLAUDE_CONFIG_DIR%\.credentials.json, else %USERPROFILE%\.claude\.credentials.json).
          "credentialsPath": null,

          // When the stored access token has expired and Claude Code has not refreshed it,
          // refresh it ourselves using the stored refresh token and write the result back so
          // Claude Code keeps working. Set false to be strictly read-only.
          "allowTokenRefresh": true,

          // Which usage window to show in the model-specific row. null = auto: the weekly limit
          // scoped to Fable (key "weekly_scoped:Fable"), else any key mentioning "fable", else any
          // model-scoped weekly limit, else "seven_day_opus". Run the exe with --dump to see the raw
          // response in usage-dump.json next to this file.
          "fableLimitKey": null,

          // Running Claude Code sessions, read from Claude Code's own session registry.
          // "expanded": one line per session (dot, name, "working" / "waiting 12m").
          // "collapsed": a single summary line ("Sessions  2 waiting · 1 working").
          // "off": hide the section.
          "sessionsMode": "expanded",

          // Sessions that have been waiting for input longer than this are not shown.
          "sessionsHideIdleAfterHours": 3,

          // How to label a session. "folder-title": project folder plus the name you gave the
          // session with /rename, or failing that Claude Code's own short title for it, e.g.
          // "ntk-cartographer · UX reviewer subagent". Also: "folder", "title", "prompt" (your
          // last message, truncated), "folder-prompt". Hovering a row shows all of them.
          "sessionsLabel": "folder-title",

          // Also list background (non-terminal) sessions.
          "sessionsIncludeBackground": false,

          // Play a short chime when a listed session finishes working and is waiting for you.
          // Also toggled from the tray menu. soundVolume is 0.0 - 1.0. Run the exe with --chime
          // to hear it once (this also writes chime.wav next to this file).
          // A session must stay waiting this long before it chimes. Claude Code briefly marks a
          // session idle when a background subagent finishes and hands its result back, so
          // shorter values risk a chime while the session is still working.
          "soundOnWaiting": false,
          "soundVolume": 0.5,
          "soundConfirmSeconds": 5
        }
        """;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        PropertyNameCaseInsensitive = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        AllowTrailingCommas = true,
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
    };

    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;

    public string Dir { get; }
    public string ConfigPath { get; }
    public string StatePath { get; }
    public string LogPath { get; }
    public string DumpPath { get; }

    /// <summary>--demo: never write config.json or state.json, so a demo run leaves the real setup alone.</summary>
    public bool ReadOnly { get; set; }

    public event Action? ConfigChanged;

    /// <summary>Set when this run moved the settings over from the app's former name.</summary>
    public string? MigratedFrom { get; }

    public ConfigStore()
    {
        var appData = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        Dir = Path.Combine(appData, "ClaudeUsageIndicator");
        // The app used to be called LlmUsageIndicator; carry its settings over once.
        var legacy = Path.Combine(appData, "LlmUsageIndicator");
        if (!Directory.Exists(Dir) && Directory.Exists(legacy))
        {
            Directory.Move(legacy, Dir);
            MigratedFrom = legacy;
        }
        Directory.CreateDirectory(Dir);
        ConfigPath = Path.Combine(Dir, "config.json");
        StatePath = Path.Combine(Dir, "state.json");
        LogPath = Path.Combine(Dir, "log.txt");
        DumpPath = Path.Combine(Dir, "usage-dump.json");
    }

    /// <summary>Load config, falling back to defaults if the file is unreadable (startup use).</summary>
    public AppConfig LoadConfig() => TryLoadConfig() ?? new AppConfig();

    /// <summary>Load config, or null if the file cannot currently be read or parsed.</summary>
    public AppConfig? TryLoadConfig()
    {
        try
        {
            if (!File.Exists(ConfigPath))
            {
                File.WriteAllText(ConfigPath, DefaultConfigText);
                Log.Info($"Wrote default config to {ConfigPath}");
            }
            var cfg = JsonSerializer.Deserialize<AppConfig>(File.ReadAllText(ConfigPath), JsonOptions) ?? new AppConfig();
            cfg.Opacity = Math.Clamp(cfg.Opacity, 0.1, 1.0);
            cfg.Scale = Math.Clamp(cfg.Scale, 0.5, 4.0);
            cfg.PollIntervalSeconds = Math.Max(10, cfg.PollIntervalSeconds);
            cfg.MaxPollIntervalSeconds = Math.Max(cfg.PollIntervalSeconds, cfg.MaxPollIntervalSeconds);
            cfg.SessionsMode = (cfg.SessionsMode ?? "").Trim().ToLowerInvariant() switch
            {
                "off" or "none" or "false" => "off",
                "collapsed" or "compact" => "collapsed",
                _ => "expanded",
            };
            cfg.SessionsHideIdleAfterHours = Math.Max(0, cfg.SessionsHideIdleAfterHours);
            cfg.CompactMode = (cfg.CompactMode ?? "").Trim().ToLowerInvariant() switch
            {
                "always" or "on" or "true" => "always",
                "never" or "off" or "false" => "never",
                _ => "auto",
            };
            cfg.SessionsLabel = (cfg.SessionsLabel ?? "").Trim().ToLowerInvariant() switch
            {
                "folder" => "folder",
                "title" => "title",
                "prompt" => "prompt",
                "folder-prompt" => "folder-prompt",
                _ => "folder-title",
            };
            cfg.SoundVolume = Math.Clamp(cfg.SoundVolume, 0, 1);
            cfg.SoundConfirmSeconds = Math.Clamp(cfg.SoundConfirmSeconds, 0, 120);
            return cfg;
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to read config: {ex.Message}");
            return null;
        }
    }

    /// <summary>
    /// Change one top-level numeric/boolean/string value in config.json by editing the text in
    /// place, so the user's comments and layout survive. Inserts the key if it is missing.
    /// </summary>
    public bool SetConfigValue(string key, string jsonLiteral)
    {
        if (ReadOnly) return false;
        try
        {
            var text = File.Exists(ConfigPath) ? File.ReadAllText(ConfigPath) : DefaultConfigText;
            var pattern = new System.Text.RegularExpressions.Regex(
                "(\"" + System.Text.RegularExpressions.Regex.Escape(key) + "\"\\s*:\\s*)(\"(?:[^\"\\\\]|\\\\.)*\"|[^,\\s}]+)");
            string updated;
            if (pattern.IsMatch(text))
            {
                updated = pattern.Replace(text, m => m.Groups[1].Value + jsonLiteral, 1);
            }
            else
            {
                var brace = text.IndexOf('{');
                if (brace < 0) return false;
                updated = text[..(brace + 1)] + Environment.NewLine + $"  \"{key}\": {jsonLiteral}," + text[(brace + 1)..];
            }
            File.WriteAllText(ConfigPath, updated);
            Log.Info($"Config: {key} = {jsonLiteral}");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not update {key} in config: {ex.Message}");
            return false;
        }
    }

    public AppState LoadState()
    {
        try
        {
            if (File.Exists(StatePath))
            {
                return JsonSerializer.Deserialize<AppState>(File.ReadAllText(StatePath), JsonOptions) ?? new AppState();
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to read state: {ex.Message}");
        }
        return new AppState();
    }

    public void SaveState(AppState state)
    {
        if (ReadOnly) return;
        try
        {
            File.WriteAllText(StatePath, JsonSerializer.Serialize(state, JsonOptions));
        }
        catch (Exception ex)
        {
            Log.Error($"Failed to save state: {ex.Message}");
        }
    }

    /// <summary>Raise <see cref="ConfigChanged"/> on the dispatcher whenever config.json is saved.</summary>
    public void WatchConfig(Dispatcher dispatcher)
    {
        _dispatcher = dispatcher;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(400) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            ConfigChanged?.Invoke();
        };
        StartWatcher();
    }

    private Dispatcher? _dispatcher;

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = new FileSystemWatcher(Dir, Path.GetFileName(ConfigPath))
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Error += (_, e) =>
        {
            Log.Error($"Config watcher failed, restarting: {e.GetException().Message}");
            Post(StartWatcher);
        };
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        Post(() => { _debounce?.Stop(); _debounce?.Start(); });

    /// <summary>Marshal to the UI thread; silently drop if the dispatcher is already gone (shutdown race).</summary>
    private void Post(Action action)
    {
        var d = _dispatcher;
        if (d is null || d.HasShutdownStarted || _disposed) return;
        try
        {
            d.BeginInvoke(action);
        }
        catch (Exception ex)
        {
            Log.Error($"Dispatcher post failed: {ex.Message}");
        }
    }

    private bool _disposed;

    public void Dispose()
    {
        _disposed = true;
        _debounce?.Stop();
        if (_watcher is not null)
        {
            _watcher.EnableRaisingEvents = false;
            _watcher.Dispose();
            _watcher = null;
        }
    }
}
