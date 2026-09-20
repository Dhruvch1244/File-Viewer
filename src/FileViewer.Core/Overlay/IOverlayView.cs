namespace FileViewer.Core.Overlay;

/// <summary>
/// The read side of the edit overlay, as <see cref="RowResolver"/> needs it. Two implementations:
/// the live <see cref="EditOverlay"/> (lock-protected, always current) and
/// <see cref="OverlaySnapshot"/> (a frozen copy taken once at the start of a scan).
///
/// The distinction exists for one reason: every row resolution asks the overlay two questions, and
/// on the live overlay each of those takes its lock. That is irrelevant when rendering a page of
/// rows and decisive when a filter walks millions of them across every core — the lock, not the
/// decoding, becomes the bottleneck. A scan takes a snapshot once and then runs lock-free.
/// </summary>
public interface IOverlayView
{
    /// <summary>True when nothing has been edited at all — lets a scan skip per-row overlay questions entirely.</summary>
    bool IsEmpty { get; }

    RowState GetRowState(long rowIndex);

    bool TryGetCellEdits(long rowIndex, out IReadOnlyList<CellEdit> edits);

    bool TryGetAddedRowTemplate(long rowIndex, out IReadOnlyList<string> template);
}
