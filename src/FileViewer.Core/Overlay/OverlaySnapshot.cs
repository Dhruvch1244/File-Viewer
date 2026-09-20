namespace FileViewer.Core.Overlay;

/// <summary>
/// An immutable point-in-time copy of an <see cref="EditOverlay"/>, taken under its lock once and
/// then read lock-free from any number of threads — see <see cref="IOverlayView"/> for why that
/// matters. Sized by how much the user has actually edited (not by file size), so copying it is
/// cheap next to the scan it serves.
///
/// Being a snapshot is also a correctness property, not just a speed one: every row a single
/// filter pass looks at is judged against the same overlay state, instead of rows scanned later
/// seeing an edit that rows scanned earlier didn't.
/// </summary>
public sealed class OverlaySnapshot : IOverlayView
{
    private readonly Dictionary<long, RowState> _rowStates;
    private readonly Dictionary<long, CellEdit[]> _cellEdits;
    private readonly Dictionary<long, string[]> _addedRows;

    internal OverlaySnapshot(
        Dictionary<long, RowState> rowStates,
        Dictionary<long, CellEdit[]> cellEdits,
        Dictionary<long, string[]> addedRows)
    {
        _rowStates = rowStates;
        _cellEdits = cellEdits;
        _addedRows = addedRows;
    }

    public bool IsEmpty => _rowStates.Count == 0 && _cellEdits.Count == 0 && _addedRows.Count == 0;

    public RowState GetRowState(long rowIndex) => _rowStates.GetValueOrDefault(rowIndex, RowState.Normal);

    public bool TryGetCellEdits(long rowIndex, out IReadOnlyList<CellEdit> edits)
    {
        if (_cellEdits.TryGetValue(rowIndex, out CellEdit[]? found))
        {
            edits = found;
            return true;
        }
        edits = [];
        return false;
    }

    public bool TryGetAddedRowTemplate(long rowIndex, out IReadOnlyList<string> template)
    {
        if (_addedRows.TryGetValue(rowIndex, out string[]? found))
        {
            template = found;
            return true;
        }
        template = [];
        return false;
    }
}
