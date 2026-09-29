using System;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageIndicator;

public sealed record OAuthCredentials(
    string AccessToken,
    string? RefreshToken,
    DateTimeOffset? ExpiresAt,
    string? SubscriptionType)
{
    public bool IsExpired(TimeSpan slack) =>
        ExpiresAt is { } e && e - slack <= DateTimeOffset.UtcNow;
}

/// <summary>
/// Reads (and, when necessary, refreshes) the OAuth login that Claude Code stores on disk, so the
/// user never has to log in a second time.
/// </summary>
public sealed class CredentialStore
{
    /// <summary>Claude Code's public OAuth client id.</summary>
    private const string ClientId = "9d1c250a-e61b-44d9-88ed-5944d1962f5e";

    /// <summary>Used when the token endpoint does not say how long the new token lives.</summary>
    private static readonly TimeSpan FallbackLifetime = TimeSpan.FromHours(1);

    private static readonly Uri[] TokenEndpoints =
    {
        new("https://platform.claude.com/v1/oauth/token"),
        new("https://console.anthropic.com/v1/oauth/token"),
    };

    private static readonly JsonDocumentOptions LenientJson = new() { CommentHandling = JsonCommentHandling.Skip };

    public string Path { get; }

    public CredentialStore(string? configuredPath)
    {
        Path = Resolve(configuredPath);
    }

    public static string Resolve(string? configuredPath)
    {
        if (!string.IsNullOrWhiteSpace(configuredPath))
        {
            return Environment.ExpandEnvironmentVariables(configuredPath);
        }

        return System.IO.Path.Combine(ClaudePaths.ConfigDir, ".credentials.json");
    }

    public OAuthCredentials? Load()
    {
        if (!File.Exists(Path)) return null;

        using var doc = JsonDocument.Parse(File.ReadAllText(Path), LenientJson);
        if (!doc.RootElement.TryGetProperty("claudeAiOauth", out var oauth) || oauth.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        var access = GetString(oauth, "accessToken");
        if (string.IsNullOrEmpty(access)) return null;

        DateTimeOffset? expires = null;
        if (oauth.TryGetProperty("expiresAt", out var exp) && exp.ValueKind == JsonValueKind.Number && exp.TryGetInt64(out var ms))
        {
            expires = DateTimeOffset.FromUnixTimeMilliseconds(ms);
        }

        return new OAuthCredentials(access, GetString(oauth, "refreshToken"), expires, GetString(oauth, "subscriptionType"));
    }

    /// <summary>
    /// Exchange the stored refresh token for a new token pair. Does not touch the file; call
    /// <see cref="TryWriteBack"/> with the result. Throws if every endpoint fails.
    /// </summary>
    public async Task<OAuthCredentials> RefreshAsync(HttpClient http, OAuthCredentials current, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(current.RefreshToken))
        {
            throw new InvalidOperationException("No refresh token stored.");
        }

        var body = JsonSerializer.Serialize(new
        {
            grant_type = "refresh_token",
            refresh_token = current.RefreshToken,
            client_id = ClientId,
        });

        Exception? lastError = null;
        foreach (var endpoint in TokenEndpoints)
        {
            try
            {
                using var content = new StringContent(body, Encoding.UTF8, "application/json");
                using var response = await http.PostAsync(endpoint, content, ct);
                var text = await response.Content.ReadAsStringAsync(ct);

                if (!response.IsSuccessStatusCode)
                {
                    lastError = new InvalidOperationException($"{endpoint.Host}: HTTP {(int)response.StatusCode} {Truncate(text, 200)}");
                    var code = (int)response.StatusCode;
                    // A 4xx other than 404 means the server understood us and said no; do not try elsewhere.
                    if (code >= 400 && code < 500 && code != 404) break;
                    continue;
                }

                using var doc = JsonDocument.Parse(text);
                var root = doc.RootElement;
                var access = GetString(root, "access_token");
                if (string.IsNullOrEmpty(access))
                {
                    lastError = new InvalidOperationException($"{endpoint.Host}: no access_token in response");
                    continue;
                }

                var refresh = GetString(root, "refresh_token") ?? current.RefreshToken;
                var expires = root.TryGetProperty("expires_in", out var ei) && ei.ValueKind == JsonValueKind.Number
                    ? DateTimeOffset.UtcNow.AddSeconds(ei.GetDouble())
                    : DateTimeOffset.UtcNow + FallbackLifetime;

                Log.Info($"Refreshed OAuth token via {endpoint.Host}; expires {expires:u}");
                return new OAuthCredentials(access, refresh, expires, current.SubscriptionType);
            }
            catch (Exception ex) when (ex is not OperationCanceledException || !ct.IsCancellationRequested)
            {
                // Network, timeout, malformed JSON: remember and try the next endpoint.
                lastError = ex;
            }
        }

        throw lastError ?? new InvalidOperationException("Token refresh failed.");
    }

    /// <summary>
    /// Persist a refreshed token pair into Claude Code's file, preserving everything else in it.
    /// Returns true when the file ends up holding a valid pair: either ours, or one Claude Code
    /// itself rotated to in the meantime (detected by the stored refresh token no longer being the
    /// one we exchanged). Returns false when the write failed and should be retried.
    /// </summary>
    public bool TryWriteBack(OAuthCredentials updated, string exchangedRefreshToken)
    {
        var tmp = Path + ".tmp";
        try
        {
            var root = (File.Exists(Path)
                           ? JsonNode.Parse(File.ReadAllText(Path), documentOptions: LenientJson) as JsonObject
                           : null)
                       ?? new JsonObject();
            var oauth = root["claudeAiOauth"] as JsonObject ?? new JsonObject();

            var storedRefresh = oauth["refreshToken"]?.GetValue<string>();
            if (storedRefresh is not null && storedRefresh != exchangedRefreshToken)
            {
                Log.Info("Claude Code rotated its token while we were refreshing; keeping its copy.");
                return true;
            }

            oauth["accessToken"] = updated.AccessToken;
            if (updated.RefreshToken is not null) oauth["refreshToken"] = updated.RefreshToken;
            if (updated.ExpiresAt is { } e) oauth["expiresAt"] = e.ToUnixTimeMilliseconds();
            root["claudeAiOauth"] = oauth;

            File.WriteAllText(tmp, root.ToJsonString(new JsonSerializerOptions { WriteIndented = false }));
            File.Move(tmp, Path, overwrite: true);
            Log.Info("Wrote refreshed token back to Claude Code's credentials file.");
            return true;
        }
        catch (Exception ex)
        {
            Log.Error($"Could not write refreshed token back to {Path}: {ex.Message}");
            try { if (File.Exists(tmp)) File.Delete(tmp); } catch { /* best effort */ }
            return false;
        }
    }

    private static string? GetString(JsonElement obj, string name) =>
        obj.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..max] + "…";
}
