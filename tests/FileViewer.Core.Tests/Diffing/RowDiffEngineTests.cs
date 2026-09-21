using FileViewer.Core.Caching;
using FileViewer.Core.Diffing;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Tests.Diffing;

public class RowDiffEngineTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static async Task<(FileIndex Index, EditOverlay Overlay, DecodedRowCache Cache)> OpenAsync(string fixture = "minimal_valid.dif") =>
        (await FileIndexer.IndexAsync(FixturePath(fixture)), new EditOverlay(), new DecodedRowCache());

    private static RowDiffResult Compare(
        FileIndex left, EditOverlay leftOverlay, DecodedRowCache leftCache,
        FileIndex right, EditOverlay rightOverlay, DecodedRowCache rightCache,
        string keyColumn = "_ID") =>
        RowDiffEngine.Compare(left, leftOverlay, leftCache, right, rightOverlay, rightCache, keyColumn);

    [Fact]
    public async Task IdenticalFilesComparedAgainstThemselves_ReportNoDifferences()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            RowDiffResult result = Compare(index, overlay, cache, index, overlay, cache);

            Assert.True(result.IsIdentical);
            Assert.Empty(result.Entries);
            Assert.Equal(3, result.UnchangedCount);
        }
    }

    [Fact]
    public async Task ChangedCellOnOneSide_ReportsAChangedEntryWithTheDifferingColumn()
    {
        (FileIndex left, EditOverlay leftOverlay, DecodedRowCache leftCache) = await OpenAsync();
        (FileIndex right, EditOverlay rightOverlay, DecodedRowCache rightCache) = await OpenAsync();
        using (left) using (right)
        {
            rightOverlay.EditCell(1, "PRICE", "999.99");

            RowDiffResult result = Compare(left, leftOverlay, leftCache, right, rightOverlay, rightCache);

            Assert.Equal(1, result.ChangedCount);
            Assert.Equal(2, result.UnchangedCount);
            RowDiffEntry entry = Assert.Single(result.Entries);
            Assert.Equal(RowDiffKind.Changed, entry.Kind);
            Assert.Equal("SEC002 HK Equity", entry.Key);
            ColumnDiff diff = Assert.Single(entry.ColumnDiffs);
            Assert.Equal("PRICE", diff.Column);
            Assert.Equal("101.25", diff.LeftValue);
            Assert.Equal("999.99", diff.RightValue);
        }
    }

    [Fact]
    public async Task RowDeletedOnTheRight_ReportsRemoved()
    {
        (FileIndex left, EditOverlay leftOverlay, DecodedRowCache leftCache) = await OpenAsync();
        (FileIndex right, EditOverlay rightOverlay, DecodedRowCache rightCache) = await OpenAsync();
        using (left) using (right)
        {
            rightOverlay.DeleteRow(0);

            RowDiffResult result = Compare(left, leftOverlay, leftCache, right, rightOverlay, rightCache);

            Assert.Equal(1, result.RemovedCount);
            Assert.Equal(0, result.AddedCount);
            RowDiffEntry entry = Assert.Single(result.Entries);
            Assert.Equal(RowDiffKind.Removed, entry.Kind);
            Assert.Equal("SEC001 HK Equity", entry.Key);
            Assert.Equal(0, entry.LeftRowIndex);
            Assert.Null(entry.RightRowIndex);
        }
    }

    [Fact]
    public async Task RowAddedOnTheRight_ReportsAdded()
    {
        (FileIndex left, EditOverlay leftOverlay, DecodedRowCache leftCache) = await OpenAsync();
        (FileIndex right, EditOverlay rightOverlay, DecodedRowCache rightCache) = await OpenAsync();
        using (left) using (right)
        {
            long newRow = rightOverlay.AddRow(right.Header.ColumnNames);
            rightOverlay.EditCell(newRow, "_ID", "SEC004 HK Equity");
            rightOverlay.EditCell(newRow, "PRICE", "50.00");

            RowDiffResult result = Compare(left, leftOverlay, leftCache, right, rightOverlay, rightCache);

            Assert.Equal(1, result.AddedCount);
            RowDiffEntry entry = Assert.Single(result.Entries);
            Assert.Equal(RowDiffKind.Added, entry.Kind);
            Assert.Equal("SEC004 HK Equity", entry.Key);
            Assert.Null(entry.LeftRowIndex);
            Assert.Equal(newRow, entry.RightRowIndex);
        }
    }

    [Fact]
    public async Task MissingKeyColumnOnEitherSide_ReportsItRatherThanComparing()
    {
        (FileIndex left, EditOverlay leftOverlay, DecodedRowCache leftCache) = await OpenAsync();
        (FileIndex right, EditOverlay rightOverlay, DecodedRowCache rightCache) = await OpenAsync();
        using (left) using (right)
        {
            RowDiffResult result = Compare(left, leftOverlay, leftCache, right, rightOverlay, rightCache, keyColumn: "NO_SUCH_COLUMN");

            Assert.True(result.KeyColumnMissingOnLeft);
            Assert.True(result.KeyColumnMissingOnRight);
            Assert.Empty(result.Entries);
        }
    }

    [Fact]
    public async Task DuplicateKeyValues_AreFlaggedRatherThanSilentlyDropped()
    {
        (FileIndex left, EditOverlay leftOverlay, DecodedRowCache leftCache) = await OpenAsync();
        (FileIndex right, EditOverlay rightOverlay, DecodedRowCache rightCache) = await OpenAsync();
        using (left) using (right)
        {
            // Row 1's _ID now collides with row 0's — the key column is no longer unique on the left.
            leftOverlay.EditCell(1, "_ID", "SEC001 HK Equity");

            RowDiffResult result = Compare(left, leftOverlay, leftCache, right, rightOverlay, rightCache);

            Assert.True(result.DuplicateKeysOnLeft);
            Assert.False(result.DuplicateKeysOnRight);
        }
    }

    [Fact]
    public async Task DeletedRows_AreExcludedFromComparisonEntirely()
    {
        (FileIndex left, EditOverlay leftOverlay, DecodedRowCache leftCache) = await OpenAsync();
        (FileIndex right, EditOverlay rightOverlay, DecodedRowCache rightCache) = await OpenAsync();
        using (left) using (right)
        {
            // Deleted on both sides — a row that's simply gone from both is not a difference.
            leftOverlay.DeleteRow(2);
            rightOverlay.DeleteRow(2);

            RowDiffResult result = Compare(left, leftOverlay, leftCache, right, rightOverlay, rightCache);

            Assert.True(result.IsIdentical);
            Assert.Equal(2, result.UnchangedCount);
        }
    }

    [Fact]
    public async Task ComparedColumns_IsTheIntersectionOfBothSidesColumns()
    {
        (FileIndex left, EditOverlay leftOverlay, DecodedRowCache leftCache) = await OpenAsync();
        (FileIndex right, EditOverlay rightOverlay, DecodedRowCache rightCache) = await OpenAsync("FixedIncomeAsia.dif");
        using (left) using (right)
        {
            RowDiffResult result = Compare(left, leftOverlay, leftCache, right, rightOverlay, rightCache);

            Assert.All(result.ComparedColumns, column => Assert.Contains(column, left.Header.ColumnNames));
            Assert.All(result.ComparedColumns, column => Assert.Contains(column, right.Header.ColumnNames));
        }
    }
}
