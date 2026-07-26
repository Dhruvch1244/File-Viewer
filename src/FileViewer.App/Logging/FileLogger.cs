using System.IO;

namespace FileViewer.App.Logging;

/// <summary>
/// Local log file writer (PRS §10 deployment: "Local log file"). Appends timestamped lines and
/// never throws — a broken log path (e.g. a locked-down profile directory) must not itself crash
/// the app, since this exists specifically to help diagnose failures gracefully (PRS §8).
/// </summary>
public sealed class FileLogger
{
    private readonly string? _logFilePath;
    private readonly object _lock = new();

    public static FileLogger Instance { get; } = new();

    private FileLogger()
    {
        try
        {
            string logDirectory = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "BloombergFileViewer", "logs");
            Directory.CreateDirectory(logDirectory);
            _logFilePath = Path.Combine(logDirectory, $"fileviewer-{DateTime.Now:yyyyMMdd}.log");
        }
        catch
        {
            _logFilePath = null; // logging becomes a no-op rather than a startup failure
        }
    }

    public void LogInfo(string message) => Write("INFO", message, null);

    public void LogWarning(string message) => Write("WARN", message, null);

    public void LogError(string message, Exception? exception = null) => Write("ERROR", message, exception);

    private void Write(string level, string message, Exception? exception)
    {
        if (_logFilePath is null) return;

        try
        {
            string line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
            if (exception is not null)
            {
                line += Environment.NewLine + exception;
            }

            lock (_lock)
            {
                File.AppendAllText(_logFilePath, line + Environment.NewLine);
            }
        }
        catch
        {
            // Logging must never itself crash the app.
        }
    }
}
