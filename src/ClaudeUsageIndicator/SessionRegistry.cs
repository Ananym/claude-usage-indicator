using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Windows.Threading;

namespace ClaudeUsageIndicator;

/// <summary>
/// A running Claude Code session as seen from its registry file plus a little transcript
/// metadata. Observed <see cref="Status"/> values: "busy" (model turn, or subagents still
/// running), "shell" (turn over but a background shell command is still running and will resume
/// the session), "idle" (waiting for the user). Only "idle" means the user is needed.
/// </summary>
public sealed record SessionInfo(
    int Pid,
    string SessionId,
    string Name,
    string NameSource,
    string Cwd,
    string Kind,
    string Status,
    DateTimeOffset StatusSince,
    string? Title,
    string? LastPrompt,
    string? CustomTitle)
{
    public bool Waiting => Status.Equals("idle", StringComparison.OrdinalIgnoreCase);
    public bool Busy => Status.Equals("busy", StringComparison.OrdinalIgnoreCase);

    /// <summary>Last path segment of the working directory.</summary>
    public string Folder
    {
        get
        {
            var f = Path.GetFileName(Cwd.TrimEnd('\\', '/'));
            return string.IsNullOrEmpty(f) ? Cwd : f;
        }
    }

    /// <summary>
    /// A name the user or Claude Code chose, as opposed to the derived "folder-xx" default. The
    /// registry name wins; a /rename recorded in the transcript ("custom-title") is the fallback.
    /// </summary>
    public string? ChosenName =>
        !NameSource.Equals("derived", StringComparison.OrdinalIgnoreCase) && !string.IsNullOrWhiteSpace(Name) ? Name
        : !string.IsNullOrWhiteSpace(CustomTitle) ? CustomTitle
        : null;

    /// <summary>
    /// Compose the display label. Styles: "folder", "title", "prompt", "folder-title", "folder-prompt".
    /// A chosen name (from /rename or Claude Code's auto-naming) replaces the title part.
    /// </summary>
    public string Label(string style)
    {
        var title = ChosenName ?? Title;
        var prompt = LastPrompt;
        return style switch
        {
            "folder" => Folder,
            "title" => title ?? Folder,
            "prompt" => prompt ?? title ?? Folder,
            "folder-prompt" => Join(Folder, prompt ?? title),
            _ => Join(Folder, title),
        };
    }

    private static string Join(string a, string? b) => string.IsNullOrWhiteSpace(b) ? a : $"{a} · {b}";
}

/// <summary>
/// Reads Claude Code's live session registry (~/.claude/sessions/&lt;pid&gt;.json). A file only counts
/// if its process is still alive <i>and</i> was started at the recorded time, which rules out
/// stale files and reused PIDs.
/// </summary>
public sealed class SessionRegistry : IDisposable
{
    private static readonly JsonDocumentOptions Lenient = new() { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true };
    private const int PromptMaxChars = 60;
    private const int TailBytes = 96 * 1024;

    private readonly string _dir = ClaudePaths.SessionsDir;
    private FileSystemWatcher? _watcher;
    private DispatcherTimer? _debounce;
    private Dispatcher? _dispatcher;
    private bool _disposed;
    private readonly IReadOnlyList<SessionInfo>? _fixed;

    /// <summary>Sessions found on the last scan, already filtered per <see cref="Scan"/>.</summary>
    public IReadOnlyList<SessionInfo> Sessions { get; private set; } = Array.Empty<SessionInfo>();

    public event Action? Changed;

    public SessionRegistry() { }

    /// <summary>Serve a fixed list (--demo) instead of reading the registry; <see cref="Watch"/> does nothing.</summary>
    public SessionRegistry(IReadOnlyList<SessionInfo> fixedSessions)
    {
        _fixed = fixedSessions;
    }

    /// <summary>Re-read the registry. Returns true when the visible set changed.</summary>
    public bool Scan(bool includeBackground, TimeSpan hideIdleAfter)
    {
        var found = new List<SessionInfo>();
        try
        {
            if (_fixed is not null) found.AddRange(_fixed);
            else if (Directory.Exists(_dir))
            {
                foreach (var file in Directory.EnumerateFiles(_dir, "*.json"))
                {
                    var info = ReadOne(file);
                    if (info is null) continue;
                    if (!includeBackground && !info.Kind.Equals("interactive", StringComparison.OrdinalIgnoreCase)) continue;
                    if (info.Waiting && DateTimeOffset.UtcNow - info.StatusSince > hideIdleAfter) continue;
                    found.Add(info);
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Session registry scan failed: {ex.Message}");
        }

        // Waiting sessions first (most recently finished at the top), then busy ones.
        found.Sort((a, b) =>
        {
            if (a.Waiting != b.Waiting) return a.Waiting ? -1 : 1;
            return b.StatusSince.CompareTo(a.StatusSince);
        });

        var changed = !SameSet(Sessions, found);
        Sessions = found;
        return changed;
    }

    private SessionInfo? ReadOne(string file)
    {
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(file), Lenient);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return null;

            if (!root.TryGetProperty("pid", out var pidEl) || !pidEl.TryGetInt32(out var pid)) return null;
            var procStart = GetString(root, "procStart");
            if (!IsAlive(pid, procStart)) return null;

            var status = GetString(root, "status") ?? "idle";
            var since = root.TryGetProperty("statusUpdatedAt", out var su) && su.TryGetInt64(out var ms)
                ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
                : root.TryGetProperty("startedAt", out var st) && st.TryGetInt64(out var ms2)
                    ? DateTimeOffset.FromUnixTimeMilliseconds(ms2)
                    : DateTimeOffset.UtcNow;

            var cwd = GetString(root, "cwd") ?? "";
            var sessionId = GetString(root, "sessionId") ?? "";
            var name = GetString(root, "name") ?? "";
            var meta = ReadTranscriptMeta(cwd, sessionId);

            return new SessionInfo(
                pid,
                sessionId,
                name,
                GetString(root, "nameSource") ?? "derived",
                cwd,
                GetString(root, "kind") ?? "interactive",
                status.Trim().ToLowerInvariant(),
                since,
                meta.Title,
                meta.LastPrompt,
                meta.CustomTitle);
        }
        catch (Exception ex)
        {
            // Half-written file or an unreadable process: skip this one, keep the rest.
            Log.Error($"Session file {Path.GetFileName(file)} skipped: {ex.Message}");
            return null;
        }
    }

    /// <summary>The PID exists and its creation time (as a FILETIME string) matches the registry.</summary>
    private static bool IsAlive(int pid, string? procStart)
    {
        try
        {
            using var process = Process.GetProcessById(pid);
            if (process.HasExited) return false;
            if (string.IsNullOrEmpty(procStart)) return true;
            return process.StartTime.ToFileTimeUtc().ToString() == procStart;
        }
        catch (ArgumentException)
        {
            return false; // no such process
        }
        catch (Exception)
        {
            return false; // access denied etc.: not ours, treat as gone
        }
    }

    // --- transcript metadata (AI title, last prompt) --------------------------------------------

    private readonly record struct TranscriptMeta(string? Title, string? LastPrompt, string? CustomTitle);

    private sealed class CacheEntry
    {
        public long Length;
        public DateTime Written;
        public TranscriptMeta Meta;
    }

    private readonly Dictionary<string, CacheEntry> _cache = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Claude Code appends "ai-title" and "last-prompt" lines to the transcript on every turn, so
    /// the newest ones sit near the end of the file. Read only the tail; fall back to a full pass
    /// for very long turns. Cached by file length and mtime.
    /// </summary>
    private TranscriptMeta ReadTranscriptMeta(string cwd, string sessionId)
    {
        if (string.IsNullOrEmpty(cwd) || string.IsNullOrEmpty(sessionId)) return default;
        var path = TranscriptPath(cwd, sessionId);
        try
        {
            if (!File.Exists(path)) return default;
            var fi = new FileInfo(path);
            if (_cache.TryGetValue(path, out var cached) && cached.Length == fi.Length && cached.Written == fi.LastWriteTimeUtc)
            {
                return cached.Meta;
            }

            var meta = ScanForMeta(path, fi.Length, tailOnly: true);
            if (meta.Title is null && meta.LastPrompt is null && meta.CustomTitle is null && fi.Length > TailBytes)
            {
                meta = ScanForMeta(path, fi.Length, tailOnly: false);
            }
            _cache[path] = new CacheEntry { Length = fi.Length, Written = fi.LastWriteTimeUtc, Meta = meta };
            return meta;
        }
        catch (Exception ex)
        {
            Log.Error($"Transcript metadata for {sessionId} unavailable: {ex.Message}");
            return default;
        }
    }

    private static TranscriptMeta ScanForMeta(string path, long length, bool tailOnly)
    {
        string? title = null, prompt = null, custom = null;
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (tailOnly && length > TailBytes) stream.Seek(length - TailBytes, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8);
        if (tailOnly && length > TailBytes) reader.ReadLine(); // discard the partial first line

        string? line;
        while ((line = reader.ReadLine()) is not null)
        {
            var isTitle = line.Contains("\"ai-title\"", StringComparison.Ordinal);
            var isPrompt = !isTitle && line.Contains("\"last-prompt\"", StringComparison.Ordinal);
            var isCustom = !isTitle && !isPrompt && line.Contains("\"custom-title\"", StringComparison.Ordinal);
            if (!isTitle && !isPrompt && !isCustom) continue;
            try
            {
                using var doc = JsonDocument.Parse(line);
                var root = doc.RootElement;
                var type = GetString(root, "type");
                if (type == "ai-title") title = Clean(GetString(root, "aiTitle"), 80) ?? title;
                else if (type == "last-prompt") prompt = Clean(GetString(root, "lastPrompt"), PromptMaxChars) ?? prompt;
                else if (type == "custom-title") custom = Clean(GetString(root, "customTitle"), 80) ?? custom;
            }
            catch (JsonException)
            {
                // torn line while Claude Code is writing; ignore
            }
        }
        return new TranscriptMeta(title, prompt, custom);
    }

    /// <summary>~/.claude/projects/&lt;cwd with non-alphanumerics as '-'&gt;/&lt;sessionId&gt;.jsonl</summary>
    private static string TranscriptPath(string cwd, string sessionId)
    {
        var encoded = Regex.Replace(cwd, "[^A-Za-z0-9]", "-");
        return Path.Combine(ClaudePaths.ConfigDir, "projects", encoded, sessionId + ".jsonl");
    }

    private static string? Clean(string? text, int max)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var collapsed = Regex.Replace(text, @"\s+", " ").Trim();
        return collapsed.Length <= max ? collapsed : collapsed[..(max - 1)].TrimEnd() + "…";
    }

    private static bool SameSet(IReadOnlyList<SessionInfo> a, IReadOnlyList<SessionInfo> b)
    {
        if (a.Count != b.Count) return false;
        for (var i = 0; i < a.Count; i++)
        {
            if (a[i] != b[i]) return false;
        }
        return true;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    // --- live updates ------------------------------------------------------------------------

    /// <summary>Raise <see cref="Changed"/> (debounced, on the dispatcher) whenever a registry file changes.</summary>
    public void Watch(Dispatcher dispatcher)
    {
        if (_fixed is not null) return;
        _dispatcher = dispatcher;
        _debounce = new DispatcherTimer(DispatcherPriority.Background, dispatcher) { Interval = TimeSpan.FromMilliseconds(300) };
        _debounce.Tick += (_, _) =>
        {
            _debounce.Stop();
            Changed?.Invoke();
        };
        StartWatcher();
    }

    private void StartWatcher()
    {
        _watcher?.Dispose();
        _watcher = null;
        if (!Directory.Exists(_dir)) return;

        _watcher = new FileSystemWatcher(_dir, "*.json")
        {
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.Size | NotifyFilters.FileName,
        };
        _watcher.Changed += OnFileEvent;
        _watcher.Created += OnFileEvent;
        _watcher.Deleted += OnFileEvent;
        _watcher.Renamed += OnFileEvent;
        _watcher.Error += (_, e) =>
        {
            Log.Error($"Session watcher failed, restarting: {e.GetException().Message}");
            Post(StartWatcher);
        };
        _watcher.EnableRaisingEvents = true;
    }

    private void OnFileEvent(object sender, FileSystemEventArgs e) =>
        Post(() => { _debounce?.Stop(); _debounce?.Start(); });

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
