using System;
using Microsoft.Win32;

namespace ClaudeUsageIndicator;

/// <summary>Registers / unregisters the app in the per-user Run key so it starts with Windows.</summary>
internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "ClaudeUsageIndicator";
    private const string LegacyValueName = "LlmUsageIndicator";

    private static string ExePath => Environment.ProcessPath ?? throw new InvalidOperationException("Cannot determine executable path.");

    public static bool IsEnabled
    {
        get
        {
            try
            {
                using var key = Registry.CurrentUser.OpenSubKey(RunKey);
                return key?.GetValue(ValueName) is string value
                    && value.Contains(ExePath, StringComparison.OrdinalIgnoreCase);
            }
            catch (Exception ex)
            {
                Log.Error($"Could not read Run key: {ex.Message}");
                return false;
            }
        }
    }

    /// <summary>
    /// The app used to register as LlmUsageIndicator, pointing at the old exe. Drop that entry and,
    /// if it was on, turn startup on for this exe instead.
    /// </summary>
    public static void MigrateLegacy()
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(RunKey, writable: true);
            if (key?.GetValue(LegacyValueName) is null) return;
            key.DeleteValue(LegacyValueName, throwOnMissingValue: false);
            Log.Info("Removed legacy LlmUsageIndicator startup entry.");
        }
        catch (Exception ex)
        {
            Log.Error($"Could not migrate legacy Run key: {ex.Message}");
            return;
        }
        if (!IsEnabled) Set(true);
    }

    public static void Set(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.CreateSubKey(RunKey, writable: true)
                            ?? throw new InvalidOperationException("Cannot open Run key.");
            if (enabled)
            {
                key.SetValue(ValueName, $"\"{ExePath}\"");
                Log.Info($"Run at startup enabled: {ExePath}");
            }
            else
            {
                key.DeleteValue(ValueName, throwOnMissingValue: false);
                Log.Info("Run at startup disabled.");
            }
        }
        catch (Exception ex)
        {
            Log.Error($"Could not update Run key: {ex.Message}");
        }
    }
}
