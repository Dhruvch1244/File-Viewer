using System.Text.Json;

namespace FileViewer.App.Settings;

/// <summary>
/// The handful of things the app should remember between runs: which theme you chose and which
/// files you had open recently. Stored as JSON next to the logs, under
/// <c>%LOCALAPPDATA%\BloombergFileViewer</c>.
///
/// Every operation here is best-effort and silent on failure. Settings are a convenience, so a
/// corrupt file, a locked directory, or a roaming profile that isn't writable must degrade to
/// "no remembered settings" rather than stop the app from opening — the same reliability rule the
/// file parser follows (PRS §8).
/// </summary>
public sealed class AppSettings
{
    /// <summary>How many recent files are kept. Long enough to cover a working session, short enough to stay a menu rather than a history.</summary>
    public const int MaxRecentFiles = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = true };

    /// <summary>"Light" or "Dark"; null if the user has never toggled it.</summary>
    public string? Theme { get; set; }

    /// <summary>Most recently opened file paths, newest first.</summary>
    public List<string> RecentFiles { get; set; } = [];

    /// <summary>Rows shown per page: a row count, 0 for "fit to window", -1 for "all rows". Null until the user picks one.</summary>
    public int? RowsPerPage { get; set; }

    public static string SettingsFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BloombergFileViewer",
        "settings.json");

    /// <summary>
    /// Where a failure to read or write settings is reported. Set by the app to its log; left null
    /// in tests, where the point is that a failure changes nothing observable.
    /// </summary>
    public static Action<string>? OnWarning { get; set; }

    public static AppSettings Load()
    {
        try
        {
            string path = SettingsFilePath;
            if (!File.Exists(path)) return new AppSettings();

            return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(path)) ?? new AppSettings();
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"Could not read settings ({ex.Message}); starting with defaults.");
            return new AppSettings();
        }
    }

    public void Save()
    {
        try
        {
            string path = SettingsFilePath;
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            File.WriteAllText(path, JsonSerializer.Serialize(this, SerializerOptions));
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"Could not save settings ({ex.Message}).");
        }
    }

    /// <summary>
    /// Moves <paramref name="path"/> to the front of the recent list (removing any earlier entry for
    /// the same file, case-insensitively — Windows paths) and trims the list to
    /// <see cref="MaxRecentFiles"/>.
    /// </summary>
    public void RememberRecentFile(string path)
    {
        RecentFiles.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));
        RecentFiles.Insert(0, path);
        if (RecentFiles.Count > MaxRecentFiles)
        {
            RecentFiles.RemoveRange(MaxRecentFiles, RecentFiles.Count - MaxRecentFiles);
        }
    }

    public void ForgetRecentFile(string path) =>
        RecentFiles.RemoveAll(existing => string.Equals(existing, path, StringComparison.OrdinalIgnoreCase));

    /// <summary>Recent files that still exist on disk — a list full of dead entries is worse than a short one.</summary>
    public IEnumerable<string> ExistingRecentFiles() => RecentFiles.Where(File.Exists);
}
