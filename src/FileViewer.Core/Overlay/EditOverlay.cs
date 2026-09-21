namespace FileViewer.Core.Overlay;

/// <summary>
/// Cell edits and row operations (add/delete/duplicate) layered on top of the base file — never
/// mutating it (PRS §6.6). Ordinary managed collections are the right call here: the overlay is
/// bounded by how much a user actually edits, unlike the row index/sort keys.
///
/// Row-index semantics: keys refer to the row's stable base index (position in
/// <see cref="Native.RowIndexEntry"/>/file order) for existing rows. Added/Duplicated rows get
/// synthetic negative indices minted here, so they can never collide with a real base index and
/// "is this an added row" is a trivial sign check.
///
/// Every public member is protected by a single lock: rows are resolved (read) from whichever
/// thread is rendering/exporting/sorting/filtering at the time, including a background thread
/// while the UI thread keeps rendering the page currently on screen — the two can genuinely
/// overlap. All access goes through methods (never a raw exposed collection you could iterate
/// outside the lock), so there is no way to accidentally bypass the lock from a new call site.
///
/// <see cref="GetPendingRowOps"/> is a snapshot of the row-structural operations. The undo stack
/// itself is separate and covers cell edits too, so Ctrl+Z reverses whatever the user last did — a
/// mistyped cell being the case that matters most, since without it the only way back was to close
/// the file and lose everything else along with it.
/// </summary>
public sealed class EditOverlay : IOverlayView
{
    private readonly object _gate = new();
    private readonly Dictionary<long, List<CellEdit>> _cellEdits = new();
    private readonly List<RowOp> _rowOps = new();
    private readonly Dictionary<long, RowState> _rowStateIndex = new();
    private readonly Dictionary<long, string[]> _addedRowData = new();

    /// <summary>
    /// The undo stack proper: one entry per undoable change, newest last, covering cell edits as
    /// well as row operations. <see cref="_rowOps"/> stays the record of row-structural changes
    /// (which is what "is this row added" is derived from); this says what order everything happened
    /// in, so Ctrl+Z reverses the last thing the user actually did rather than the last row
    /// operation with any number of cell edits piled on top of it.
    /// </summary>
    private readonly List<UndoEntry> _undo = new();

    private long _nextSyntheticIndex = -1;

    private enum UndoKind
    {
        RowOp,
        CellEdit,
    }

    private readonly record struct UndoEntry(UndoKind Kind, long RowIndex);

    public bool CanUndo { get { lock (_gate) return _undo.Count > 0; } }

    /// <summary>True when nothing has been edited: no cell edits, no added/duplicated rows, no deletions.</summary>
    public bool IsEmpty
    {
        get { lock (_gate) return _cellEdits.Count == 0 && _rowOps.Count == 0 && _rowStateIndex.Count == 0 && _undo.Count == 0; }
    }

    /// <summary>
    /// Captures enough of the current state to reconstruct it later via
    /// <see cref="RestorePersistedState"/> — not the undo history, just what the user would
    /// currently see. Used for crash-recovery persistence (<see cref="OverlayRecoveryStore"/>):
    /// written out periodically so unsaved edits survive the app closing without a clean export.
    /// </summary>
    public OverlayPersistedState CapturePersistedState()
    {
        lock (_gate)
        {
            if (_rowOps.Count == 0 && _cellEdits.Count == 0) return OverlayPersistedState.Empty;

            var cellEdits = new Dictionary<long, CellEdit[]>(_cellEdits.Count);
            foreach ((long rowIndex, List<CellEdit> edits) in _cellEdits)
            {
                cellEdits[rowIndex] = [.. edits];
            }
            return new OverlayPersistedState(
                [.. _rowOps],
                new Dictionary<long, RowState>(_rowStateIndex),
                cellEdits,
                new Dictionary<long, string[]>(_addedRowData),
                _nextSyntheticIndex);
        }
    }

    /// <summary>
    /// Replaces this overlay's current state with a previously captured one — restoring unsaved
    /// edits after a crash. Only safe to call on a freshly opened overlay: it replaces everything
    /// rather than merging. The undo stack starts empty; recovery restores the data, not the
    /// ability to step back further than the recovery point.
    /// </summary>
    public void RestorePersistedState(OverlayPersistedState state)
    {
        lock (_gate)
        {
            _rowOps.Clear();
            _rowOps.AddRange(state.RowOps);

            _rowStateIndex.Clear();
            foreach ((long rowIndex, RowState rowState) in state.RowStates)
            {
                _rowStateIndex[rowIndex] = rowState;
            }

            _cellEdits.Clear();
            foreach ((long rowIndex, CellEdit[] edits) in state.CellEdits)
            {
                _cellEdits[rowIndex] = [.. edits];
            }

            _addedRowData.Clear();
            foreach ((long rowIndex, string[] fields) in state.AddedRows)
            {
                _addedRowData[rowIndex] = fields;
            }

            _nextSyntheticIndex = state.NextSyntheticIndex;
            _undo.Clear();
        }
    }

    /// <summary>
    /// Freezes the current overlay state into a lock-free <see cref="OverlaySnapshot"/>. Taken once
    /// per filter/sort/export scan so that walking millions of rows doesn't take this lock twice per
    /// row — see <see cref="IOverlayView"/>.
    /// </summary>
    public OverlaySnapshot CreateSnapshot()
    {
        lock (_gate)
        {
            var rowStates = new Dictionary<long, RowState>(_rowStateIndex);
            var cellEdits = new Dictionary<long, CellEdit[]>(_cellEdits.Count);
            foreach ((long rowIndex, List<CellEdit> edits) in _cellEdits)
            {
                cellEdits[rowIndex] = [.. edits];
            }
            var addedRows = new Dictionary<long, string[]>(_addedRowData.Count);
            foreach ((long rowIndex, string[] fields) in _addedRowData)
            {
                addedRows[rowIndex] = fields;
            }
            return new OverlaySnapshot(rowStates, cellEdits, addedRows);
        }
    }

    /// <summary>Records (or replaces, for repeated edits to the same cell) an edit. Last write for a given column wins during resolution.</summary>
    public void EditCell(long rowIndex, string column, string newValue)
    {
        lock (_gate)
        {
            if (!_cellEdits.TryGetValue(rowIndex, out List<CellEdit>? edits))
            {
                edits = [];
                _cellEdits[rowIndex] = edits;
            }
            edits.Add(new CellEdit(rowIndex, column, newValue));
            _undo.Add(new UndoEntry(UndoKind.CellEdit, rowIndex));
        }
    }

    /// <summary>Snapshot of the edits recorded for a row, or false if it has none. The returned list is a copy — safe to enumerate after the call regardless of what happens on another thread afterward.</summary>
    public bool TryGetCellEdits(long rowIndex, out IReadOnlyList<CellEdit> edits)
    {
        lock (_gate)
        {
            if (_cellEdits.TryGetValue(rowIndex, out List<CellEdit>? found))
            {
                edits = [.. found];
                return true;
            }
            edits = [];
            return false;
        }
    }

    /// <summary>Adds a new blank row (all columns empty) and returns its synthetic row index.</summary>
    public long AddRow(IReadOnlyList<string> columnNames)
    {
        lock (_gate)
        {
            long newIndex = _nextSyntheticIndex--;
            var blank = new string[columnNames.Count];
            Array.Fill(blank, string.Empty);
            _addedRowData[newIndex] = blank;
            PushOpNoLock(RowOpType.Add, newIndex);
            return newIndex;
        }
    }

    /// <summary>
    /// Adds a new row seeded from <paramref name="resolvedSourceFields"/> — the caller must pass
    /// the source row's already-*resolved* fields (edits included), not raw file bytes, so
    /// duplicating an edited row duplicates what the user currently sees.
    /// </summary>
    public long DuplicateRow(IReadOnlyList<string> resolvedSourceFields)
    {
        lock (_gate)
        {
            long newIndex = _nextSyntheticIndex--;
            _addedRowData[newIndex] = [.. resolvedSourceFields];
            PushOpNoLock(RowOpType.Duplicate, newIndex);
            return newIndex;
        }
    }

    /// <summary>Snapshot of an added/duplicated row's template fields, or false if <paramref name="rowIndex"/> isn't one (or was undone). The returned list is a copy.</summary>
    public bool TryGetAddedRowTemplate(long rowIndex, out IReadOnlyList<string> template)
    {
        lock (_gate)
        {
            if (_addedRowData.TryGetValue(rowIndex, out string[]? found))
            {
                template = found;
                return true;
            }
            template = [];
            return false;
        }
    }

    public void DeleteRow(long rowIndex)
    {
        lock (_gate) PushOpNoLock(RowOpType.Delete, rowIndex);
    }

    /// <summary>Bulk delete is N independent single-row operations — undo reverses them one row at a time.</summary>
    public void BulkDelete(IEnumerable<long> rowIndices)
    {
        lock (_gate)
        {
            foreach (long rowIndex in rowIndices)
            {
                PushOpNoLock(RowOpType.Delete, rowIndex);
            }
        }
    }

    /// <summary>
    /// Directly reverses the most recent still-pending operation on <paramref name="rowIndex"/>
    /// (expected to be a Delete, on a currently-deleted row) without touching any other row's
    /// history — unlike <see cref="Undo"/>, which always reverses whichever operation is globally
    /// most recent. Returns false if no pending operation exists for this row.
    /// </summary>
    public bool RestoreRow(long rowIndex)
    {
        lock (_gate)
        {
            for (int i = _rowOps.Count - 1; i >= 0; i--)
            {
                if (_rowOps[i].RowIndex == rowIndex)
                {
                    RowOp op = _rowOps[i];
                    _rowOps.RemoveAt(i);
                    RemoveLastUndoEntryNoLock(UndoKind.RowOp, rowIndex);
                    ApplyReversalNoLock(op);
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>
    /// Reverses the single most recent change (LIFO), whether that was a row operation or a cell
    /// edit. Undoing a cell edit drops the last recorded value for that cell, which — since edits
    /// resolve in order with the last one winning — restores whatever the cell showed before it:
    /// the previous edit, or the file's own value if there was none.
    /// </summary>
    public void Undo()
    {
        lock (_gate)
        {
            if (_undo.Count == 0) return;

            UndoEntry entry = _undo[^1];
            _undo.RemoveAt(_undo.Count - 1);

            if (entry.Kind == UndoKind.CellEdit)
            {
                UndoCellEditNoLock(entry.RowIndex);
                return;
            }

            if (_rowOps.Count == 0) return;
            RowOp op = _rowOps[^1];
            _rowOps.RemoveAt(_rowOps.Count - 1);
            ApplyReversalNoLock(op);
        }
    }

    private void UndoCellEditNoLock(long rowIndex)
    {
        if (!_cellEdits.TryGetValue(rowIndex, out List<CellEdit>? edits) || edits.Count == 0) return;

        edits.RemoveAt(edits.Count - 1);
        if (edits.Count == 0)
        {
            _cellEdits.Remove(rowIndex);
        }
    }

    public RowState GetRowState(long rowIndex)
    {
        lock (_gate) return GetRowStateNoLock(rowIndex);
    }

    /// <summary>Snapshot of the pending row-op undo stack, oldest first. Rarely needed outside tests — production callers that just need "which added/duplicated rows are still live" should use <see cref="GetLiveAddedOrDuplicatedRowIndices"/> instead of re-deriving it from this.</summary>
    public IReadOnlyList<RowOp> GetPendingRowOps()
    {
        lock (_gate) return [.. _rowOps];
    }

    /// <summary>
    /// Every currently-live Added/Duplicated row's synthetic index, in creation order — the same
    /// "which added rows should still appear" computation every base-row-order builder (session
    /// export order, the grid's effective row order, the toolbar's arbitrary-column sort candidate
    /// list) needs, kept here so it's both lock-protected and not duplicated at each call site.
    /// </summary>
    public IReadOnlyList<long> GetLiveAddedOrDuplicatedRowIndices()
    {
        lock (_gate)
        {
            var result = new List<long>();
            foreach (RowOp op in _rowOps)
            {
                if ((op.Type is RowOpType.Add or RowOpType.Duplicate) && GetRowStateNoLock(op.RowIndex) != RowState.Deleted)
                {
                    result.Add(op.RowIndex);
                }
            }
            return result;
        }
    }

    private void RemoveLastUndoEntryNoLock(UndoKind kind, long rowIndex)
    {
        for (int i = _undo.Count - 1; i >= 0; i--)
        {
            if (_undo[i].Kind == kind && _undo[i].RowIndex == rowIndex)
            {
                _undo.RemoveAt(i);
                return;
            }
        }
    }

    private RowState GetRowStateNoLock(long rowIndex) => _rowStateIndex.GetValueOrDefault(rowIndex, RowState.Normal);

    private void PushOpNoLock(RowOpType type, long rowIndex)
    {
        RowState previousState = GetRowStateNoLock(rowIndex);
        RowState newState = type switch
        {
            RowOpType.Add => RowState.Added,
            RowOpType.Duplicate => RowState.Duplicated,
            RowOpType.Delete => RowState.Deleted,
            RowOpType.Restore => RowState.Normal,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        _rowOps.Add(new RowOp(type, rowIndex, previousState));
        _undo.Add(new UndoEntry(UndoKind.RowOp, rowIndex));
        _rowStateIndex[rowIndex] = newState;
    }

    private void ApplyReversalNoLock(RowOp op)
    {
        switch (op.Type)
        {
            case RowOpType.Add:
            case RowOpType.Duplicate:
                // Undoing an add/duplicate discards it entirely, including any edits made to it
                // while it existed only in the overlay — so those edits must leave the undo stack
                // too, or a later Ctrl+Z would try to reverse an edit to a row that no longer exists.
                _rowStateIndex.Remove(op.RowIndex);
                _addedRowData.Remove(op.RowIndex);
                _cellEdits.Remove(op.RowIndex);
                _undo.RemoveAll(entry => entry.Kind == UndoKind.CellEdit && entry.RowIndex == op.RowIndex);
                break;

            case RowOpType.Delete:
                if (op.PreviousState == RowState.Normal)
                {
                    _rowStateIndex.Remove(op.RowIndex);
                }
                else
                {
                    _rowStateIndex[op.RowIndex] = op.PreviousState;
                }
                break;

            case RowOpType.Restore:
                // Only reachable if a Restore op were ever pushed onto the row-op list (RestoreRow
                // above reverses Delete ops directly rather than pushing its own entry); kept for
                // completeness against the RowOpType enum.
                _rowStateIndex[op.RowIndex] = RowState.Deleted;
                break;
        }
    }
}
