using FileViewer.Core.Overlay;

namespace FileViewer.Core.Tests.Overlay;

public class EditOverlayTests
{
    private static readonly string[] Columns = ["_ID", "_ERR", "PRICE"];

    [Fact]
    public void AddRow_ReturnsNegativeSyntheticIndex_AndTracksBlankTemplate()
    {
        var overlay = new EditOverlay();

        long index = overlay.AddRow(Columns);

        Assert.True(index < 0);
        Assert.Equal(RowState.Added, overlay.GetRowState(index));
        Assert.True(overlay.TryGetAddedRowTemplate(index, out IReadOnlyList<string> template));
        Assert.Equal(new[] { "", "", "" }, template);
    }

    [Fact]
    public void AddRow_CalledTwice_ReturnsDistinctDecreasingIndices()
    {
        var overlay = new EditOverlay();

        long first = overlay.AddRow(Columns);
        long second = overlay.AddRow(Columns);

        Assert.NotEqual(first, second);
        Assert.True(second < first);
    }

    [Fact]
    public void AddRow_ThenUndo_RemovesStateTemplateAndEdits()
    {
        var overlay = new EditOverlay();
        long index = overlay.AddRow(Columns);
        overlay.EditCell(index, "_ID", "NEW_ID");

        overlay.Undo();

        Assert.Equal(RowState.Normal, overlay.GetRowState(index));
        Assert.False(overlay.TryGetAddedRowTemplate(index, out _));
        Assert.False(overlay.TryGetCellEdits(index, out _));
    }

    [Fact]
    public void DuplicateRow_StoresProvidedResolvedSnapshotVerbatim()
    {
        var overlay = new EditOverlay();
        string[] resolvedSourceFields = ["SEC001 HK Equity", "0", "EDITED_PRICE"];

        long index = overlay.DuplicateRow(resolvedSourceFields);

        Assert.Equal(RowState.Duplicated, overlay.GetRowState(index));
        Assert.True(overlay.TryGetAddedRowTemplate(index, out IReadOnlyList<string> template));
        Assert.Equal(resolvedSourceFields, template);
    }

    [Fact]
    public void DuplicateRow_SnapshotIsIndependentOfSourceArrayMutation()
    {
        var overlay = new EditOverlay();
        string[] resolvedSourceFields = ["SEC001 HK Equity", "0", "100.00"];

        long index = overlay.DuplicateRow(resolvedSourceFields);
        resolvedSourceFields[2] = "MUTATED_AFTER_DUPLICATE";

        Assert.True(overlay.TryGetAddedRowTemplate(index, out IReadOnlyList<string> template));
        Assert.Equal("100.00", template[2]);
    }

    [Fact]
    public void DeleteRow_ThenUndo_RestoresBaseRowToNormal()
    {
        var overlay = new EditOverlay();

        overlay.DeleteRow(5);
        Assert.Equal(RowState.Deleted, overlay.GetRowState(5));

        overlay.Undo();

        Assert.Equal(RowState.Normal, overlay.GetRowState(5));
    }

    [Fact]
    public void AddedRow_ThenDeleted_ThenUndoOfDelete_RestoresAddedStateNotNormal()
    {
        // This is the PreviousState chain the PRS §12 risk calls out: a flat state map would lose
        // the fact this row was Added once Deleted overwrote it.
        var overlay = new EditOverlay();
        long index = overlay.AddRow(Columns);
        overlay.DeleteRow(index);
        Assert.Equal(RowState.Deleted, overlay.GetRowState(index));

        overlay.Undo(); // undoes the Delete, not the Add

        Assert.Equal(RowState.Added, overlay.GetRowState(index));
        Assert.True(overlay.TryGetAddedRowTemplate(index, out _)); // template must still be intact
    }

    [Fact]
    public void RestoreRow_ReversesOnlyTheTargetedRowWithoutTouchingLaterUnrelatedOps()
    {
        var overlay = new EditOverlay();
        overlay.DeleteRow(1);
        overlay.DeleteRow(2);

        bool restored = overlay.RestoreRow(1);

        Assert.True(restored);
        Assert.Equal(RowState.Normal, overlay.GetRowState(1));
        Assert.Equal(RowState.Deleted, overlay.GetRowState(2)); // untouched
        IReadOnlyList<RowOp> pendingOps = overlay.GetPendingRowOps();
        Assert.Single(pendingOps); // row 1's delete op was consumed; row 2's remains pending
        Assert.Equal(2, pendingOps[0].RowIndex);
    }

    [Fact]
    public void RestoreRow_WithNoPendingOpForThatRow_ReturnsFalse()
    {
        var overlay = new EditOverlay();

        Assert.False(overlay.RestoreRow(999));
    }

    [Fact]
    public void BulkDelete_PushesIndependentOpsUndoableOneRowAtATime()
    {
        var overlay = new EditOverlay();

        overlay.BulkDelete([1, 2, 3]);

        Assert.Equal(RowState.Deleted, overlay.GetRowState(1));
        Assert.Equal(RowState.Deleted, overlay.GetRowState(2));
        Assert.Equal(RowState.Deleted, overlay.GetRowState(3));
        Assert.Equal(3, overlay.GetPendingRowOps().Count);

        overlay.Undo(); // reverses only row 3

        Assert.Equal(RowState.Normal, overlay.GetRowState(3));
        Assert.Equal(RowState.Deleted, overlay.GetRowState(1));
        Assert.Equal(RowState.Deleted, overlay.GetRowState(2));
    }

    [Fact]
    public void Undo_OnEmptyStack_IsNoOp()
    {
        var overlay = new EditOverlay();

        var exception = Record.Exception(overlay.Undo);

        Assert.Null(exception);
        Assert.False(overlay.CanUndo);
    }

    [Fact]
    public void CanUndo_ReflectsWhetherRowOpsArePending()
    {
        var overlay = new EditOverlay();
        Assert.False(overlay.CanUndo);

        overlay.DeleteRow(1);
        Assert.True(overlay.CanUndo);

        overlay.Undo();
        Assert.False(overlay.CanUndo);
    }

    [Fact]
    public void EditCell_OnDeletedRow_IsStoredButDoesNotAffectRowState()
    {
        var overlay = new EditOverlay();
        overlay.DeleteRow(1);

        overlay.EditCell(1, "_ID", "SHOULD_BE_INERT");

        Assert.Equal(RowState.Deleted, overlay.GetRowState(1));
        Assert.True(overlay.TryGetCellEdits(1, out IReadOnlyList<CellEdit> edits));
        Assert.Single(edits);
    }

    [Fact]
    public void GetLiveAddedOrDuplicatedRowIndices_ExcludesDeletedAddedRows()
    {
        var overlay = new EditOverlay();
        long kept = overlay.AddRow(Columns);
        long deleted = overlay.AddRow(Columns);
        overlay.DeleteRow(deleted);

        IReadOnlyList<long> live = overlay.GetLiveAddedOrDuplicatedRowIndices();

        Assert.Contains(kept, live);
        Assert.DoesNotContain(deleted, live);
    }

    [Fact]
    public void ConcurrentReadsAndWrites_DoNotCorruptState()
    {
        // The property this whole redesign exists for: EditOverlay is read from a background
        // thread (arbitrary-column sort/filter) while the UI thread keeps editing/adding/deleting
        // rows. This doesn't assert exact interleaving (that's inherently racy) — it asserts that
        // running both concurrently for a while never throws or corrupts internal state, and that
        // every add this test performs is still individually resolvable afterward.
        var overlay = new EditOverlay();
        const int iterations = 2000;
        var addedIndices = new System.Collections.Concurrent.ConcurrentBag<long>();

        Task writer = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                long index = overlay.AddRow(Columns);
                addedIndices.Add(index);
                overlay.EditCell(index, "_ID", $"ROW{i}");
                if (i % 7 == 0) overlay.DeleteRow(index);
            }
        });

        Task reader = Task.Run(() =>
        {
            for (int i = 0; i < iterations; i++)
            {
                _ = overlay.GetLiveAddedOrDuplicatedRowIndices();
                _ = overlay.GetPendingRowOps();
                _ = overlay.CanUndo;
            }
        });

        var exception = Record.Exception(() => Task.WaitAll(writer, reader));

        Assert.Null(exception);
        Assert.Equal(iterations, addedIndices.Count);
        foreach (long index in addedIndices)
        {
            Assert.True(overlay.TryGetAddedRowTemplate(index, out _));
        }
    }
}
