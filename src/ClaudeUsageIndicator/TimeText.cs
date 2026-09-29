using System;
using System.Globalization;

namespace ClaudeUsageIndicator;

internal static class TimeText
{
    /// <summary>"14:30 · 2h 13m", "Thu 09:00 · 3d 4h", "14:30 · now".</summary>
    public static string FormatReset(DateTimeOffset resetsAt, DateTimeOffset now, bool use24HourClock)
    {
        var culture = CultureInfo.CurrentCulture;
        var local = resetsAt.ToLocalTime();
        var nowLocal = now.ToLocalTime();
        var timeFormat = use24HourClock ? "HH:mm" : "h:mm tt";

        string absolute;
        if (local.Date == nowLocal.Date)
        {
            absolute = local.ToString(timeFormat, culture);
        }
        else if ((local - nowLocal).TotalDays < 6)
        {
            absolute = local.ToString("ddd " + timeFormat, culture);
        }
        else
        {
            absolute = local.ToString("d MMM " + timeFormat, culture);
        }

        var remaining = resetsAt - now;
        var relative = remaining <= TimeSpan.Zero ? "now" : Duration(remaining);
        return $"{absolute} · {relative}";
    }

    public static string Duration(TimeSpan t)
    {
        if (t < TimeSpan.Zero) t = TimeSpan.Zero;
        if (t.TotalDays >= 1) return $"{(int)t.TotalDays}d {t.Hours}h";
        if (t.TotalHours >= 1) return $"{(int)t.TotalHours}h {t.Minutes}m";
        if (t.TotalMinutes >= 1) return $"{(int)t.TotalMinutes}m";
        return $"{t.Seconds}s";
    }

    public static string Ago(TimeSpan t) => t.TotalSeconds < 5 ? "just now" : Duration(t) + " ago";

    /// <summary>Terse freshness marker: "↻ now", "↻ 4m".</summary>
    public static string Freshness(TimeSpan t) => "↻ " + (t.TotalSeconds < 5 ? "now" : Duration(t));
}
