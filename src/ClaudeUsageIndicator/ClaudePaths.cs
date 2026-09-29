using System;
using System.IO;

namespace ClaudeUsageIndicator;

/// <summary>Where Claude Code keeps its per-user state on this machine.</summary>
internal static class ClaudePaths
{
    /// <summary>%CLAUDE_CONFIG_DIR% if set, else %USERPROFILE%\.claude.</summary>
    public static string ConfigDir
    {
        get
        {
            var dir = Environment.GetEnvironmentVariable("CLAUDE_CONFIG_DIR");
            if (!string.IsNullOrWhiteSpace(dir)) return dir;
            return Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".claude");
        }
    }

    public static string SessionsDir => Path.Combine(ConfigDir, "sessions");
}
