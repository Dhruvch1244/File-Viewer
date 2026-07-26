using FileViewer.Core.Caching;
using FileViewer.Core.Indexing;
using FileViewer.Core.Native;
using FileViewer.Core.Overlay;
using FileViewer.Core.Sorting;

namespace FileViewer.Core.Tests.Overlay;

public class RowResolverTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static async Task<FileIndex> LoadAsync(string fileName) => await FileIndexer.IndexAsync(FixturePath(fileName));

    [Fact]
    public async Task Resolve_UnmodifiedRow_ReturnsRawFieldsWithNormalState()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        ResolvedRow? resolved = RowResolver.Resolve(0, index, overlay, cache);

        Assert.NotNull(resolved);
        Assert.Equal(["SEC001 HK Equity", "0", "100.50"], resolved.FieldValues);
        Assert.Equal(RowRenderState.Normal, resolved.RenderState);
    }

    [Fact]
    public async Task Resolve_EditedCell_AppliesEditAndReportsEditedState()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        overlay.EditCell(0, "PRICE", "999.99");
        ResolvedRow? resolved = RowResolver.Resolve(0, index, overlay, cache);

        Assert.NotNull(resolved);
        Assert.Equal(["SEC001 HK Equity", "0", "999.99"], resolved.FieldValues);
        Assert.Equal(RowRenderState.Edited, resolved.RenderState);
    }

    [Fact]
    public async Task Resolve_MultipleEditsToSameColumn_LastWriteWins()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        overlay.EditCell(0, "PRICE", "111.00");
        overlay.EditCell(0, "PRICE", "222.00");
        ResolvedRow? resolved = RowResolver.Resolve(0, index, overlay, cache);

        Assert.Equal("222.00", resolved!.FieldValues[2]);
    }

    [Fact]
    public async Task Resolve_DeletedRow_ReturnsNullTombstone()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        overlay.DeleteRow(0);

        Assert.Null(RowResolver.Resolve(0, index, overlay, cache));
    }

    [Fact]
    public async Task Resolve_EditThenDelete_EditIsInertWhileDeleted()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        overlay.EditCell(0, "PRICE", "999.99");
        overlay.DeleteRow(0);

        Assert.Null(RowResolver.Resolve(0, index, overlay, cache));
        Assert.Single(overlay.CellEdits[0]); // edit is retained, not discarded
    }

    [Fact]
    public async Task Resolve_EditThenDeleteThenUndo_EditReappliesAutomatically()
    {
        // The concrete resolution of the PRS §12 risk: an edit on a since-deleted row is inert,
        // not lost — restoring the row makes it take effect again with no extra bookkeeping.
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        overlay.EditCell(0, "PRICE", "999.99");
        overlay.DeleteRow(0);
        overlay.Undo();

        ResolvedRow? resolved = RowResolver.Resolve(0, index, overlay, cache);

        Assert.NotNull(resolved);
        Assert.Equal("999.99", resolved.FieldValues[2]);
        Assert.Equal(RowRenderState.Edited, resolved.RenderState);
    }

    [Fact]
    public async Task Resolve_AddedRow_ReturnsTemplateFieldsWithAddedState()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        long newIndex = overlay.AddRow(index.Header.ColumnNames);
        overlay.EditCell(newIndex, "_ID", "NEW_SECURITY");

        ResolvedRow? resolved = RowResolver.Resolve(newIndex, index, overlay, cache);

        Assert.NotNull(resolved);
        Assert.Equal(RowRenderState.Added, resolved.RenderState); // Added wins over "has edits" in the render-state precedence
        Assert.Equal("NEW_SECURITY", resolved.FieldValues[0]);
    }

    [Fact]
    public async Task DuplicateRow_CapturesResolvedSnapshot_IndependentOfLaterSourceEdits()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        overlay.EditCell(0, "PRICE", "500.00");
        ResolvedRow? sourceAtDuplicateTime = RowResolver.Resolve(0, index, overlay, cache);
        long duplicateIndex = overlay.DuplicateRow(sourceAtDuplicateTime!.FieldValues);

        // Further edits to the original after duplicating must not retroactively affect the copy.
        overlay.EditCell(0, "PRICE", "999.00");

        ResolvedRow? original = RowResolver.Resolve(0, index, overlay, cache);
        ResolvedRow? duplicate = RowResolver.Resolve(duplicateIndex, index, overlay, cache);

        Assert.Equal("999.00", original!.FieldValues[2]);
        Assert.Equal("500.00", duplicate!.FieldValues[2]);
        Assert.Equal(RowRenderState.Duplicated, duplicate.RenderState);
    }

    [Fact]
    public async Task Resolve_MalformedRow_ReportsMalformedEvenWhenAlsoEdited()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("bad_column_count.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        // Row 1 ("SEC002 HK Equity|0") has only 2 fields against the 3-column header.
        overlay.EditCell(1, "_ID", "EDITED_ANYWAY");

        ResolvedRow? resolved = RowResolver.Resolve(1, index, overlay, cache);

        Assert.NotNull(resolved);
        Assert.Equal(RowRenderState.Malformed, resolved.RenderState); // malformed takes precedence over edited
        Assert.Equal("EDITED_ANYWAY", resolved.FieldValues[0]); // the edit itself still applies
    }

    [Fact]
    public async Task Resolve_UnaffectedByReSort_EditStaysAttachedToCorrectBaseRow()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        overlay.EditCell(1, "PRICE", "SORTED_TEST"); // row 1 = SEC002

        using UnmanagedArray<SortKey> descending = RowSorter.SortByKey(index.SortKeys, SortDirection.Descending);
        // Sorting only permutes the SortKey array; base row index 1 is untouched and still resolves
        // with its edit regardless of where it now sits in the sorted view.
        bool foundEditedRowInSortedView = false;
        for (int i = 0; i < (int)descending.Count; i++)
        {
            if (descending[i].RowIndex == 1)
            {
                foundEditedRowInSortedView = true;
            }
        }
        Assert.True(foundEditedRowInSortedView);

        ResolvedRow? resolvedAfterSort = RowResolver.Resolve(1, index, overlay, cache);
        Assert.Equal("SORTED_TEST", resolvedAfterSort!.FieldValues[2]);
    }

    [Fact]
    public async Task Resolve_CachesBaseDecodeForUnmodifiedRow_OnSecondCallDoesNotReparse()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        RowResolver.Resolve(0, index, overlay, cache);
        Assert.Equal(1, cache.Count);

        // A second resolve should hit the cache (same content) rather than re-parsing; verified
        // indirectly by confirming the cache entry is still present and the result is unchanged.
        ResolvedRow? resolved = RowResolver.Resolve(0, index, overlay, cache);
        Assert.Equal(1, cache.Count);
        Assert.Equal("100.50", resolved!.FieldValues[2]);
    }

    [Fact]
    public async Task Resolve_AddedRow_DoesNotPopulateDecodedRowCache()
    {
        using FileIndex index = await LoadAsync("minimal_valid.dif");
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        long newIndex = overlay.AddRow(index.Header.ColumnNames);
        RowResolver.Resolve(newIndex, index, overlay, cache);

        Assert.Equal(0, cache.Count);
    }
}
