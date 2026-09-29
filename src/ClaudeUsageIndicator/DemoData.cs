using System;
using System.Collections.Generic;
using System.Globalization;

namespace ClaudeUsageIndicator;

/// <summary>
/// Canned usage numbers and sessions for <c>--demo</c>, which renders the overlay without touching
/// the network, the session registry or the user's saved state (for README screenshots). Times
/// are relative to launch, so countdowns and "waiting" durations tick like the real thing.
/// </summary>
public static class DemoData
{
    public static UsageSnapshot Snapshot(DateTimeOffset now)
    {
        // One bar per colour band, each a little ahead of its time marker.
        var json = $$"""
            {
              "five_hour": { "utilization": 64.0, "resets_at": "{{Iso(now + new TimeSpan(2, 5, 0))}}" },
              "seven_day": { "utilization": 38.0, "resets_at": "{{Iso(now + new TimeSpan(4, 7, 30, 0))}}" },
              "limits": [
                { "kind": "weekly_scoped", "percent": 83.0, "resets_at": "{{Iso(now + new TimeSpan(1, 22, 15, 0))}}",
                  "scope": { "model": { "display_name": "Fable" } } }
              ]
            }
            """;
        return UsageSnapshot.Parse(json);
    }

    /// <summary>Three sessions, one in each state Claude Code reports: idle (waiting), busy, shell.</summary>
    public static IReadOnlyList<SessionInfo> Sessions(DateTimeOffset now) => new[]
    {
        new SessionInfo(41208, "3f6c1a9e-52d4-4b8a-9e1f-7c20d8a4b613", "", "derived", @"C:\dev\web-shop", "interactive",
            "idle", now - TimeSpan.FromMinutes(3), "Fix checkout rounding bug", "yes, go ahead and add the test", null),
        new SessionInfo(38116, "9b2d7e40-18c3-4f6a-a5d2-e3b917c05f88", "", "derived", @"C:\dev\api-gateway", "interactive",
            "busy", now - TimeSpan.FromMinutes(1), "Migrate auth middleware to JWT", "keep the old session cookies working for a week", null),
        new SessionInfo(52904, "c7a05f13-6e9b-4d21-8c47-0a5f2b9d31e6", "", "derived", @"C:\dev\data-pipeline", "interactive",
            "shell", now - TimeSpan.FromMinutes(6), "Backfill September events", "run the backfill and tell me when it's done", null),
    };

    private static string Iso(DateTimeOffset t) => t.UtcDateTime.ToString("yyyy-MM-dd'T'HH:mm:ss'Z'", CultureInfo.InvariantCulture);
}
