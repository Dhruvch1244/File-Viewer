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
/// <see cref="RowOps"/> is the undo stack (append/pop-only). Cell edits are intentionally not part
/// of it — only row-structural operations (add/delete/duplicate/restore) are individually
/// undoable, matching the PRS §9 data-model sketch, which comments <c>RowOps</c> alone as "also the
/// undo stack".
/// </summary>
public sealed class EditOverlay
{
    private long _nextSyntheticIndex = -1;

    public Dictionary<long, List<CellEdit>> CellEdits { get; } = new();
    public List<RowOp> RowOps { get; } = new();
    public Dictionary<long, RowState> RowStateIndex { get; } = new();
    public Dictionary<long, string[]> AddedRowData { get; } = new();

    public bool CanUndo => RowOps.Count > 0;

    /// <summary>Records (or replaces, for repeated edits to the same cell) an edit. Last write for a given column wins during resolution.</summary>
    public void EditCell(long rowIndex, string column, string newValue)
    {
        if (!CellEdits.TryGetValue(rowIndex, out List<CellEdit>? edits))
        {
            edits = [];
            CellEdits[rowIndex] = edits;
        }
        edits.Add(new CellEdit(rowIndex, column, newValue));
    }

    /// <summary>Adds a new blank row (all columns empty) and returns its synthetic row index.</summary>
    public long AddRow(IReadOnlyList<string> columnNames)
    {
        long newIndex = _nextSyntheticIndex--;
        var blank = new string[columnNames.Count];
        Array.Fill(blank, string.Empty);
        AddedRowData[newIndex] = blank;
        PushOp(RowOpType.Add, newIndex);
        return newIndex;
    }

    /// <summary>
    /// Adds a new row seeded from <paramref name="resolvedSourceFields"/> — the caller must pass
    /// the source row's already-*resolved* fields (edits included), not raw file bytes, so
    /// duplicating an edited row duplicates what the user currently sees.
    /// </summary>
    public long DuplicateRow(IReadOnlyList<string> resolvedSourceFields)
    {
        long newIndex = _nextSyntheticIndex--;
        AddedRowData[newIndex] = [.. resolvedSourceFields];
        PushOp(RowOpType.Duplicate, newIndex);
        return newIndex;
    }

    public void DeleteRow(long rowIndex) => PushOp(RowOpType.Delete, rowIndex);

    /// <summary>Bulk delete is N independent single-row operations — undo reverses them one row at a time.</summary>
    public void BulkDelete(IEnumerable<long> rowIndices)
    {
        foreach (long rowIndex in rowIndices)
        {
            DeleteRow(rowIndex);
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
        for (int i = RowOps.Count - 1; i >= 0; i--)
        {
            if (RowOps[i].RowIndex == rowIndex)
            {
                RowOp op = RowOps[i];
                RowOps.RemoveAt(i);
                ApplyReversal(op);
                return true;
            }
        }
        return false;
    }

    /// <summary>Reverses the single most recent row operation (LIFO).</summary>
    public void Undo()
    {
        if (RowOps.Count == 0) return;
        RowOp op = RowOps[^1];
        RowOps.RemoveAt(RowOps.Count - 1);
        ApplyReversal(op);
    }

    public RowState GetRowState(long rowIndex) => RowStateIndex.GetValueOrDefault(rowIndex, RowState.Normal);

    private void PushOp(RowOpType type, long rowIndex)
    {
        RowState previousState = GetRowState(rowIndex);
        RowState newState = type switch
        {
            RowOpType.Add => RowState.Added,
            RowOpType.Duplicate => RowState.Duplicated,
            RowOpType.Delete => RowState.Deleted,
            RowOpType.Restore => RowState.Normal,
            _ => throw new ArgumentOutOfRangeException(nameof(type), type, null),
        };

        RowOps.Add(new RowOp(type, rowIndex, previousState));
        RowStateIndex[rowIndex] = newState;
    }

    private void ApplyReversal(RowOp op)
    {
        switch (op.Type)
        {
            case RowOpType.Add:
            case RowOpType.Duplicate:
                // Undoing an add/duplicate discards it entirely, including any edits made to it
                // while it existed only in the overlay.
                RowStateIndex.Remove(op.RowIndex);
                AddedRowData.Remove(op.RowIndex);
                CellEdits.Remove(op.RowIndex);
                break;

            case RowOpType.Delete:
                if (op.PreviousState == RowState.Normal)
                {
                    RowStateIndex.Remove(op.RowIndex);
                }
                else
                {
                    RowStateIndex[op.RowIndex] = op.PreviousState;
                }
                break;

            case RowOpType.Restore:
                // Only reachable if a Restore op were ever pushed onto RowOps (RestoreRow above
                // reverses Delete ops directly rather than pushing its own entry); kept for
                // completeness against the RowOpType enum.
                RowStateIndex[op.RowIndex] = RowState.Deleted;
                break;
        }
    }
}
