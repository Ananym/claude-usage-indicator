using System;
using System.IO;

namespace ClaudeUsageIndicator;

internal static class Log
{
    private const long MaxBytes = 1024 * 1024;
    private static readonly object Gate = new();
    private static string? _path;

    public static void Init(string path) => _path = path;

    public static void Info(string message) => Write("INFO", message);
    public static void Error(string message) => Write("ERROR", message);

    private static void Write(string level, string message)
    {
        if (_path is null) return;
        try
        {
            lock (Gate)
            {
                if (File.Exists(_path) && new FileInfo(_path).Length > MaxBytes)
                {
                    File.Move(_path, _path + ".old", overwrite: true);
                }
                File.AppendAllText(_path, $"{DateTimeOffset.Now:yyyy-MM-dd HH:mm:ss zzz} [{level}] {message}{Environment.NewLine}");
            }
        }
        catch
        {
            // Logging must never take the app down.
        }
    }
}
