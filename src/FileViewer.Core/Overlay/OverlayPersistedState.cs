namespace FileViewer.Core.Overlay;

/// <summary>
/// Everything needed to reconstruct an <see cref="EditOverlay"/>'s current state — not its undo
/// history, just what the user would currently see. See <see cref="EditOverlay.CapturePersistedState"/>
/// and <see cref="EditOverlay.RestorePersistedState"/>. Plain data, deliberately: this is what gets
/// JSON-serialized to disk for crash recovery (see <see cref="OverlayRecoveryStore"/>), so every
/// member here has to round-trip through <c>System.Text.Json</c> with no custom converter.
/// </summary>
public sealed record OverlayPersistedState(
    IReadOnlyList<RowOp> RowOps,
    IReadOnlyDictionary<long, RowState> RowStates,
    IReadOnlyDictionary<long, CellEdit[]> CellEdits,
    IReadOnlyDictionary<long, string[]> AddedRows,
    long NextSyntheticIndex)
{
    public static readonly OverlayPersistedState Empty = new(
        [], new Dictionary<long, RowState>(), new Dictionary<long, CellEdit[]>(), new Dictionary<long, string[]>(), -1);

    /// <summary>True when there is nothing here worth persisting — the same condition <see cref="EditOverlay.IsEmpty"/> checks.</summary>
    public bool IsEmpty => RowOps.Count == 0 && RowStates.Count == 0 && CellEdits.Count == 0 && AddedRows.Count == 0;
}
