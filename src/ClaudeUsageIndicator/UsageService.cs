using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Threading;
using System.Threading.Tasks;

namespace ClaudeUsageIndicator;

/// <summary>
/// Ties credentials and the usage endpoint together and keeps the latest result plus a
/// human-readable status for the UI.
/// </summary>
public sealed class UsageService
{
    private readonly HttpClient _http;
    private readonly UsageClient _client;

    /// <summary>The credentials that last worked, possibly newer than what is on disk.</summary>
    private OAuthCredentials? _lastGood;

    /// <summary>A refreshed pair we still owe to Claude Code's credentials file.</summary>
    private (OAuthCredentials Updated, string Exchanged)? _unsaved;

    public AppConfig Config { get; set; }

    public UsageSnapshot? Snapshot { get; private set; }
    public DateTimeOffset? LastSuccess { get; private set; }
    public string? Error { get; private set; }
    public string? SubscriptionType { get; private set; }
    public string? CredentialsPath { get; private set; }

    /// <summary>--demo: serve <see cref="DemoData"/> instead of reading credentials or fetching.</summary>
    public bool Demo { get; init; }

    public UsageService(AppConfig config)
    {
        Config = config;
        _http = new HttpClient { Timeout = TimeSpan.FromSeconds(20) };
        _http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("ClaudeUsageIndicator", "0.1"));
        _client = new UsageClient(_http);
    }

    /// <summary>Fetch once. Never throws; failures land in <see cref="Error"/>.</summary>
    public async Task PollAsync(CancellationToken ct)
    {
        if (Demo)
        {
            // Built once so reset times stay put and the countdowns run down.
            if (Snapshot is null)
            {
                var now = DateTimeOffset.UtcNow;
                Snapshot = DemoData.Snapshot(now);
                LastSuccess = now - TimeSpan.FromMinutes(2);
                SubscriptionType = "max";
            }
            return;
        }

        string? warning = null;
        try
        {
            var store = new CredentialStore(Config.CredentialsPath);
            CredentialsPath = store.Path;

            // Settle any debt from an earlier refresh before doing anything else.
            if (_unsaved is { } pending)
            {
                if (store.TryWriteBack(pending.Updated, pending.Exchanged)) _unsaved = null;
                else warning = "Refreshed login not yet saved for Claude Code; will retry.";
            }

            var creds = store.Load();
            if (creds is null)
            {
                Error = "No Claude Code login found. Run `claude` and /login.";
                return;
            }

            // Prefer our in-memory pair when the file still holds the older, expired one.
            if (_lastGood is not null && _lastGood.AccessToken != creds.AccessToken
                && creds.IsExpired(TimeSpan.Zero) && !_lastGood.IsExpired(TimeSpan.Zero))
            {
                creds = _lastGood;
            }

            SubscriptionType = creds.SubscriptionType;

            if (creds.IsExpired(TimeSpan.FromMinutes(1)))
            {
                creds = await TryRefreshAsync(store, creds, ct) ?? creds;
            }

            try
            {
                Snapshot = await _client.FetchAsync(creds.AccessToken, ct);
            }
            catch (UsageAuthException)
            {
                // Token rejected. Maybe Claude Code rotated it under us; otherwise try refreshing.
                var reloaded = store.Load();
                if (reloaded is not null && reloaded.AccessToken != creds.AccessToken)
                {
                    creds = reloaded;
                }
                else
                {
                    var refreshed = await TryRefreshAsync(store, creds, ct);
                    if (refreshed is null) throw;
                    creds = refreshed;
                }
                Snapshot = await _client.FetchAsync(creds.AccessToken, ct);
            }

            _lastGood = creds;
            LastSuccess = DateTimeOffset.UtcNow;
            Error = _unsaved is null ? null : (warning ?? "Refreshed login not yet saved for Claude Code; will retry.");
        }
        catch (UsageAuthException ex)
        {
            Error = "Claude login rejected. Run `claude` to refresh it.";
            Log.Error($"Auth failure: {ex.Message}");
        }
        catch (OperationCanceledException)
        {
            Error = "Request timed out.";
        }
        catch (HttpRequestException ex)
        {
            Error = $"Network error: {ex.Message}";
            Log.Error($"Network failure: {ex.Message}");
        }
        catch (Exception ex)
        {
            Error = ex.Message;
            Log.Error($"Poll failed: {ex}");
        }
    }

    /// <summary>Diagnostic: return the raw JSON from the usage endpoint. Throws on failure.</summary>
    public async Task<string> FetchRawAsync(CancellationToken ct)
    {
        var store = new CredentialStore(Config.CredentialsPath);
        var creds = store.Load() ?? throw new InvalidOperationException($"No credentials at {store.Path}");
        if (creds.IsExpired(TimeSpan.FromMinutes(1)))
        {
            creds = await TryRefreshAsync(store, creds, ct) ?? creds;
        }
        return await _client.FetchRawAsync(creds.AccessToken, ct);
    }

    private async Task<OAuthCredentials?> TryRefreshAsync(CredentialStore store, OAuthCredentials creds, CancellationToken ct)
    {
        if (!Config.AllowTokenRefresh)
        {
            Log.Info("Token expired and allowTokenRefresh is false; waiting for Claude Code to refresh it.");
            return null;
        }
        if (string.IsNullOrEmpty(creds.RefreshToken))
        {
            Log.Info("Token expired and no refresh token is stored.");
            return null;
        }

        OAuthCredentials updated;
        try
        {
            updated = await store.RefreshAsync(_http, creds, ct);
        }
        catch (Exception ex)
        {
            Log.Error($"Token refresh failed: {ex.Message}");
            return null;
        }

        // From here on the old refresh token is spent: the new pair must reach the file.
        if (!store.TryWriteBack(updated, creds.RefreshToken))
        {
            _unsaved = (updated, creds.RefreshToken);
        }
        return updated;
    }
}
