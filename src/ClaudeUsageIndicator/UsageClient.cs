using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageIndicator;

/// <summary>
/// One rate-limit window as reported by Anthropic: percent used (0-100), when it resets, and an
/// optional display label (for model-scoped limits, the model's name).
/// </summary>
public sealed record LimitWindow(double Utilization, DateTimeOffset? ResetsAt, string? Label = null);

public sealed class UsageSnapshot
{
    public const string SessionKey = "five_hour";
    public const string WeeklyKey = "seven_day";

    /// <summary>Prefix for entries synthesised from the <c>limits</c> array, e.g. <c>weekly_scoped:Fable</c>.</summary>
    private const string ScopedPrefix = "weekly_scoped:";

    public DateTimeOffset FetchedAt { get; }
    public IReadOnlyDictionary<string, LimitWindow> Windows { get; }
    public string RawJson { get; }

    private UsageSnapshot(DateTimeOffset fetchedAt, IReadOnlyDictionary<string, LimitWindow> windows, string rawJson)
    {
        FetchedAt = fetchedAt;
        Windows = windows;
        RawJson = rawJson;
    }

    public LimitWindow? Get(string key) => Windows.TryGetValue(key, out var w) ? w : null;

    /// <summary>Readable summary of the numbers, for the log.</summary>
    public string ContentKey
    {
        get
        {
            var keys = new System.Collections.Generic.List<string>(Windows.Keys);
            keys.Sort(StringComparer.Ordinal);
            var sb = new System.Text.StringBuilder();
            foreach (var k in keys)
            {
                var w = Windows[k];
                sb.Append(k).Append('=').Append(w.Utilization.ToString("0.##", CultureInfo.InvariantCulture))
                  .Append('@').Append(w.ResetsAt?.ToUnixTimeSeconds() ?? 0).Append(';');
            }
            return sb.ToString();
        }
    }

    /// <summary>
    /// True when nothing anyone could see has changed since <paramref name="baseline"/>: same
    /// windows, percents within a point, reset times within a minute. The raw response wobbles
    /// below that between fetches (fractional percents, reset timestamps that straddle a second
    /// or minute boundary), so bucketed equality is not enough; it has to be a tolerance, and
    /// measured against the last fetch that counted as a change rather than the previous one so
    /// a slow creep still registers.
    /// </summary>
    public bool IsSteadyAgainst(UsageSnapshot baseline)
    {
        if (Windows.Count != baseline.Windows.Count) return false;
        foreach (var (key, w) in Windows)
        {
            if (!baseline.Windows.TryGetValue(key, out var b)) return false;
            if (Math.Abs(w.Utilization - b.Utilization) >= 1.0) return false;
            if (w.ResetsAt is null != b.ResetsAt is null) return false;
            if (w.ResetsAt is { } r && b.ResetsAt is { } br && Math.Abs((r - br).TotalSeconds) >= 60) return false;
        }
        return true;
    }

    /// <summary>
    /// Pick the model-specific weekly window. Order: the configured key; anything mentioning
    /// "fable"; any model-scoped weekly limit; the legacy <c>seven_day_opus</c> slot.
    /// </summary>
    public string? ResolveModelKey(string? configured)
    {
        if (!string.IsNullOrWhiteSpace(configured) && Windows.ContainsKey(configured)) return configured;

        foreach (var key in Windows.Keys)
        {
            if (IsWeekly(key) && key.Contains("fable", StringComparison.OrdinalIgnoreCase)) return key;
        }
        foreach (var key in Windows.Keys)
        {
            if (key.StartsWith(ScopedPrefix, StringComparison.Ordinal)) return key;
        }
        return Windows.ContainsKey("seven_day_opus") ? "seven_day_opus" : null;
    }

    /// <summary>Rolling window length implied by a key name, or null when unknown.</summary>
    public static TimeSpan? WindowLengthFor(string? key)
    {
        if (key is null) return null;
        if (key.StartsWith("five_hour", StringComparison.OrdinalIgnoreCase) || key.StartsWith("session", StringComparison.OrdinalIgnoreCase))
            return TimeSpan.FromHours(5);
        if (IsWeekly(key)) return TimeSpan.FromDays(7);
        return null;
    }

    private static bool IsWeekly(string key) =>
        key.StartsWith("weekly", StringComparison.OrdinalIgnoreCase)
        || key.StartsWith("seven_day", StringComparison.OrdinalIgnoreCase);

    /// <summary>Short human label for a window key ("Fable", "Opus", ...).</summary>
    public string LabelFor(string? key, string fallback = "Fable")
    {
        if (key is null) return fallback;
        if (Windows.TryGetValue(key, out var w) && !string.IsNullOrWhiteSpace(w.Label)) return w.Label;
        if (key.Contains("fable", StringComparison.OrdinalIgnoreCase)) return "Fable";
        if (key.Contains("opus", StringComparison.OrdinalIgnoreCase)) return "Opus";
        if (key.Contains("sonnet", StringComparison.OrdinalIgnoreCase)) return "Sonnet";
        var trimmed = key.StartsWith(ScopedPrefix, StringComparison.Ordinal) ? key[ScopedPrefix.Length..] : key;
        return trimmed.Replace("seven_day_", "", StringComparison.OrdinalIgnoreCase).Replace('_', ' ');
    }

    public static UsageSnapshot Parse(string json)
    {
        var windows = new Dictionary<string, LimitWindow>(StringComparer.Ordinal);
        using var doc = JsonDocument.Parse(json);
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new FormatException("Usage response was not a JSON object.");
        }

        // Top-level windows: { "five_hour": { "utilization": 16.0, "resets_at": "..." }, ... }
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Value.TryGetProperty("utilization", out var util) || util.ValueKind != JsonValueKind.Number) continue;
            windows[prop.Name] = new LimitWindow(util.GetDouble(), ParseResetsAt(prop.Value));
        }

        // Structured list: "limits": [ { "kind": "weekly_scoped", "percent": 42, "resets_at": "...",
        //                               "scope": { "model": { "display_name": "Fable" } } }, ... ]
        if (root.TryGetProperty("limits", out var limits) && limits.ValueKind == JsonValueKind.Array)
        {
            foreach (var limit in limits.EnumerateArray())
            {
                if (limit.ValueKind != JsonValueKind.Object) continue;
                var kind = GetString(limit, "kind");
                if (kind is null) continue;
                if (!limit.TryGetProperty("percent", out var pct) || pct.ValueKind != JsonValueKind.Number) continue;

                string? label = null;
                if (limit.TryGetProperty("scope", out var scope) && scope.ValueKind == JsonValueKind.Object
                    && scope.TryGetProperty("model", out var model) && model.ValueKind == JsonValueKind.Object)
                {
                    label = GetString(model, "display_name") ?? GetString(model, "id");
                }

                var key = label is null ? kind : $"{kind}:{label}";
                windows[key] = new LimitWindow(pct.GetDouble(), ParseResetsAt(limit), label);
            }
        }

        return new UsageSnapshot(DateTimeOffset.UtcNow, windows, json);
    }

    private static DateTimeOffset? ParseResetsAt(JsonElement obj)
    {
        if (obj.TryGetProperty("resets_at", out var r) && r.ValueKind == JsonValueKind.String
            && DateTimeOffset.TryParse(r.GetString(), CultureInfo.InvariantCulture,
                DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var parsed))
        {
            return parsed;
        }
        return null;
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

public sealed class UsageAuthException : Exception
{
    public UsageAuthException(string message) : base(message) { }
}

public sealed class UsageClient
{
    private static readonly Uri Endpoint = new("https://api.anthropic.com/api/oauth/usage");
    private readonly HttpClient _http;

    public UsageClient(HttpClient http)
    {
        _http = http;
    }

    public async Task<string> FetchRawAsync(string accessToken, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, Endpoint);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", accessToken);
        request.Headers.TryAddWithoutValidation("anthropic-beta", "oauth-2025-04-20");
        request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

        using var response = await _http.SendAsync(request, ct);
        var body = await response.Content.ReadAsStringAsync(ct);

        if (response.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
        {
            throw new UsageAuthException($"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
        }
        if (!response.IsSuccessStatusCode)
        {
            throw new HttpRequestException($"HTTP {(int)response.StatusCode}: {Truncate(body, 200)}");
        }
        return body;
    }

    public async Task<UsageSnapshot> FetchAsync(string accessToken, CancellationToken ct) =>
        UsageSnapshot.Parse(await FetchRawAsync(accessToken, ct));

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
