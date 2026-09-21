namespace FileViewer.App.Settings;

/// <summary>
/// Where crash-recovery data for unsaved edits lives — handed to
/// <c>FileViewer.Core.Overlay.OverlayRecoveryStore</c>, which knows how to read and write files
/// there but nothing about where "there" is. Kept alongside <see cref="AppSettings"/> since both
/// are paths under this app's own local state folder.
/// </summary>
public static class RecoveryPaths
{
    public static string Directory => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "BloombergFileViewer",
        "recovery");
}
