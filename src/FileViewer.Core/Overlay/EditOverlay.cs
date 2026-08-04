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
/// <see cref="GetPendingRowOps"/> is a snapshot of the undo stack (append/pop-only). Cell edits are
/// intentionally not part of it — only row-structural operations (add/delete/duplicate/restore) are
/// individually undoable, matching the PRS §9 data-model sketch, which comments the row-op list
/// alone as "also the undo stack".
/// </summary>
public sealed class EditOverlay
{
    private readonly object _gate = new();
    private readonly Dictionary<long, List<CellEdit>> _cellEdits = new();
    private readonly List<RowOp> _rowOps = new();
    private readonly Dictionary<long, RowState> _rowStateIndex = new();
    private readonly Dictionary<long, string[]> _addedRowData = new();
    private long _nextSyntheticIndex = -1;

    public bool CanUndo { get { lock (_gate) return _rowOps.Count > 0; } }

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
                    ApplyReversalNoLock(op);
                    return true;
                }
            }
            return false;
        }
    }

    /// <summary>Reverses the single most recent row operation (LIFO).</summary>
    public void Undo()
    {
        lock (_gate)
        {
            if (_rowOps.Count == 0) return;
            RowOp op = _rowOps[^1];
            _rowOps.RemoveAt(_rowOps.Count - 1);
            ApplyReversalNoLock(op);
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
        _rowStateIndex[rowIndex] = newState;
    }

    private void ApplyReversalNoLock(RowOp op)
    {
        switch (op.Type)
        {
            case RowOpType.Add:
            case RowOpType.Duplicate:
                // Undoing an add/duplicate discards it entirely, including any edits made to it
                // while it existed only in the overlay.
                _rowStateIndex.Remove(op.RowIndex);
                _addedRowData.Remove(op.RowIndex);
                _cellEdits.Remove(op.RowIndex);
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
