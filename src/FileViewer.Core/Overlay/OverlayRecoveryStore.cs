using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace FileViewer.Core.Overlay;

/// <summary>
/// Persists an <see cref="OverlayRecoveryRecord"/> to (and reads it back from) a JSON file, so
/// unsaved edits survive the app closing without a clean export — a crash, a forced kill, a
/// Windows update reboot, and so on. One file per open document (file path + section index),
/// named by a hash of that pair so it never collides with another document's recovery data and
/// never has to worry about characters a real path might contain but a file name cannot.
///
/// Deliberately knows nothing about *where* recovery files belong — that is a caller concern
/// (normally somewhere under the user's local app-data folder); every method here takes the
/// directory explicitly, the same way <see cref="Indexing.FileIndexer"/> takes an explicit path
/// rather than assuming one.
///
/// Every operation is best-effort and silent on failure, the same reliability rule
/// <c>FileViewer.App.Settings.AppSettings</c> follows: recovery is a safety net, and a safety net
/// that can crash the app it is trying to protect has failed at its one job.
/// </summary>
public static class OverlayRecoveryStore
{
    private const string FileSuffix = ".recovery.json";
    private static readonly JsonSerializerOptions SerializerOptions = new() { WriteIndented = false };

    /// <summary>
    /// Where a failure to save, load, or delete recovery data is reported. Set by the app to its
    /// log; left null in tests, where a failure changes nothing observable.
    /// </summary>
    public static Action<string>? OnWarning { get; set; }

    /// <summary>The recovery file name for a given document — stable across runs (so re-opening the same file/section finds its own recovery data) and filesystem-safe (a hash, not the path itself, which may contain characters a file name cannot).</summary>
    public static string FileNameFor(string sourceFilePath, int sectionIndex)
    {
        string key = $"{sourceFilePath.ToUpperInvariant()}\u0000{sectionIndex}";
        byte[] hash = SHA256.HashData(Encoding.UTF8.GetBytes(key));
        return Convert.ToHexString(hash)[..24].ToLowerInvariant() + FileSuffix;
    }

    /// <summary>Writes <paramref name="record"/> under <paramref name="directory"/>, replacing any existing recovery data for the same document. Never throws.</summary>
    public static void Save(string directory, OverlayRecoveryRecord record)
    {
        try
        {
            Directory.CreateDirectory(directory);
            string path = Path.Combine(directory, FileNameFor(record.SourceFilePath, record.SectionIndex));
            string json = JsonSerializer.Serialize(record, SerializerOptions);
            File.WriteAllText(path, json);
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"Could not save recovery data for '{record.SourceFilePath}' ({ex.Message}).");
        }
    }

    /// <summary>Removes the recovery data for one document, if any — called once its edits are exported or explicitly discarded, so it never resurfaces as "unsaved changes found" on a later launch. Never throws.</summary>
    public static void Delete(string directory, string sourceFilePath, int sectionIndex)
    {
        try
        {
            string path = Path.Combine(directory, FileNameFor(sourceFilePath, sectionIndex));
            if (File.Exists(path)) File.Delete(path);
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"Could not remove recovery data for '{sourceFilePath}' ({ex.Message}).");
        }
    }

    /// <summary>
    /// Every recovery record currently on disk under <paramref name="directory"/> — what the app
    /// checks at startup before any file is opened. A record that fails to parse (a partial write
    /// from a crash mid-save, a future/incompatible format) is skipped rather than failing the
    /// whole scan; a missing directory yields an empty list, not an error.
    /// </summary>
    public static IReadOnlyList<OverlayRecoveryRecord> LoadAll(string directory)
    {
        var results = new List<OverlayRecoveryRecord>();
        if (!Directory.Exists(directory)) return results;

        IEnumerable<string> files;
        try
        {
            files = Directory.EnumerateFiles(directory, "*" + FileSuffix);
        }
        catch (Exception ex)
        {
            OnWarning?.Invoke($"Could not list recovery data in '{directory}' ({ex.Message}).");
            return results;
        }

        foreach (string file in files)
        {
            try
            {
                string json = File.ReadAllText(file);
                OverlayRecoveryRecord? record = JsonSerializer.Deserialize<OverlayRecoveryRecord>(json, SerializerOptions);
                if (record is not null) results.Add(record);
            }
            catch (Exception ex)
            {
                OnWarning?.Invoke($"Could not read recovery file '{file}' ({ex.Message}); skipping it.");
            }
        }

        return results;
    }
}
