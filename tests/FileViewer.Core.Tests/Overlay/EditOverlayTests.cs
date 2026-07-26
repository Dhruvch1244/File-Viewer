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
        Assert.Equal(new[] { "", "", "" }, overlay.AddedRowData[index]);
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
        Assert.False(overlay.AddedRowData.ContainsKey(index));
        Assert.False(overlay.CellEdits.ContainsKey(index));
    }

    [Fact]
    public void DuplicateRow_StoresProvidedResolvedSnapshotVerbatim()
    {
        var overlay = new EditOverlay();
        string[] resolvedSourceFields = ["SEC001 HK Equity", "0", "EDITED_PRICE"];

        long index = overlay.DuplicateRow(resolvedSourceFields);

        Assert.Equal(RowState.Duplicated, overlay.GetRowState(index));
        Assert.Equal(resolvedSourceFields, overlay.AddedRowData[index]);
    }

    [Fact]
    public void DuplicateRow_SnapshotIsIndependentOfSourceArrayMutation()
    {
        var overlay = new EditOverlay();
        string[] resolvedSourceFields = ["SEC001 HK Equity", "0", "100.00"];

        long index = overlay.DuplicateRow(resolvedSourceFields);
        resolvedSourceFields[2] = "MUTATED_AFTER_DUPLICATE";

        Assert.Equal("100.00", overlay.AddedRowData[index][2]);
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
        Assert.True(overlay.AddedRowData.ContainsKey(index)); // template must still be intact
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
        Assert.Single(overlay.RowOps); // row 1's delete op was consumed; row 2's remains pending
        Assert.Equal(2, overlay.RowOps[0].RowIndex);
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
        Assert.Equal(3, overlay.RowOps.Count);

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
        Assert.Single(overlay.CellEdits[1]);
    }
}
