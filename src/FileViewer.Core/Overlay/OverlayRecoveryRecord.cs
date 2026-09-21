namespace FileViewer.Core.Overlay;

/// <summary>
/// One document's crash-recovery record: enough to find the source file (and, for a bulk file, the
/// specific section) again and restore its overlay exactly as it was at the last autosave.
/// </summary>
public sealed record OverlayRecoveryRecord(
    string SourceFilePath,
    int SectionIndex,
    DateTimeOffset SavedAtUtc,
    OverlayPersistedState Overlay);
