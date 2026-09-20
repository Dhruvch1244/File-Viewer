using FileViewer.Core.Caching;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;
using FileViewer.Core.Statistics;

namespace FileViewer.Core.Tests.Statistics;

public class ColumnStatisticsTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private static async Task<(FileIndex Index, EditOverlay Overlay, DecodedRowCache Cache)> OpenAsync(string fixture = "minimal_valid.dif") =>
        (await FileIndexer.IndexAsync(FixturePath(fixture)), new EditOverlay(), new DecodedRowCache());

    [Fact]
    public async Task Compute_NumericColumn_SummarizesValues()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            // minimal_valid.dif holds prices 100.50, 101.25 and 99.75.
            ColumnStatistics stats = ColumnStatisticsCalculator.Compute([0, 1, 2], "PRICE", index, overlay, cache);

            Assert.Equal(3, stats.RowCount);
            Assert.Equal(0, stats.BlankCount);
            Assert.Equal(3, stats.NonBlankCount);
            Assert.Equal(3, stats.DistinctCount);
            Assert.False(stats.DistinctTruncated);
            Assert.Equal(3, stats.NumericCount);
            Assert.True(stats.IsFullyNumeric);
            Assert.Equal(99.75, stats.NumericMin);
            Assert.Equal(101.25, stats.NumericMax);
            Assert.Equal(301.5, stats.Sum!.Value, 5);
            Assert.Equal(100.5, stats.Mean!.Value, 5);
        }
    }

    [Fact]
    public async Task Compute_UsesNumericAwareComparisonForMinAndMax()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            ColumnStatistics stats = ColumnStatisticsCalculator.Compute([0, 1, 2], "PRICE", index, overlay, cache);

            // Ordinal comparison would call "100.50" the smallest; numerically it is "99.75".
            Assert.Equal("99.75", stats.Min);
            Assert.Equal("101.25", stats.Max);
        }
    }

    [Fact]
    public async Task Compute_ColumnWithOneRepeatedValue_ReportsASingleDistinctValue()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            ColumnStatistics stats = ColumnStatisticsCalculator.Compute([0, 1, 2], "_ERR", index, overlay, cache);

            Assert.Equal(1, stats.DistinctCount);
            Assert.Equal("0", stats.Min);
            Assert.Equal("0", stats.Max);
        }
    }

    [Fact]
    public async Task Compute_NonNumericColumn_ReportsNoNumericSummary()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            ColumnStatistics stats = ColumnStatisticsCalculator.Compute([0, 1, 2], "_ID", index, overlay, cache);

            Assert.Equal(0, stats.NumericCount);
            Assert.False(stats.IsFullyNumeric);
            Assert.Null(stats.Sum);
            Assert.Null(stats.Mean);
            Assert.Null(stats.NumericMin);
        }
    }

    [Fact]
    public async Task Compute_CountsBlanksSeparatelyFromValues()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            overlay.EditCell(1, "PRICE", string.Empty);

            ColumnStatistics stats = ColumnStatisticsCalculator.Compute([0, 1, 2], "PRICE", index, overlay, cache);

            Assert.Equal(3, stats.RowCount);
            Assert.Equal(1, stats.BlankCount);
            Assert.Equal(2, stats.NonBlankCount);
            Assert.Equal(2, stats.NumericCount);
            Assert.Equal(1.0 / 3.0, stats.BlankFraction, 5);
        }
    }

    [Fact]
    public async Task Compute_ReflectsEditsAndSkipsDeletedRows()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            overlay.EditCell(0, "PRICE", "1000");
            overlay.DeleteRow(2);

            ColumnStatistics stats = ColumnStatisticsCalculator.Compute([0, 1, 2], "PRICE", index, overlay, cache);

            Assert.Equal(2, stats.RowCount);
            Assert.Equal(1000, stats.NumericMax);
            Assert.Equal(101.25, stats.NumericMin);
        }
    }

    [Fact]
    public async Task Compute_UnknownColumn_ReturnsEmptyRatherThanThrowing()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync();
        using (index)
        {
            ColumnStatistics stats = ColumnStatisticsCalculator.Compute([0, 1, 2], "NO_SUCH_COLUMN", index, overlay, cache);

            Assert.Equal(0, stats.RowCount);
            Assert.Equal(0, stats.DistinctCount);
            Assert.Null(stats.Min);
        }
    }

    [Fact]
    public async Task Compute_LargeColumn_AgreesWithAStraightforwardCount()
    {
        (FileIndex index, EditOverlay overlay, DecodedRowCache cache) = await OpenAsync("FixedIncomeAsia.dif");
        using (index)
        {
            var rows = new List<long>();
            for (long i = 0; i < (long)index.RowIndex.Count; i++) rows.Add(i);

            ColumnStatistics stats = ColumnStatisticsCalculator.Compute(rows, "CURRENCY", index, overlay, cache);

            var reference = new HashSet<string>(StringComparer.Ordinal);
            foreach (long rowIndex in rows)
            {
                ResolvedRow resolved = RowResolver.Resolve(rowIndex, index, overlay, cache)!;
                reference.Add(resolved.FieldValues[index.Header.ColumnIndexOf("CURRENCY")]);
            }

            Assert.Equal(rows.Count, stats.RowCount);
            Assert.Equal(reference.Count, stats.DistinctCount);
        }
    }

    [Theory]
    [InlineData(1234.5678, "1,234.5678")]
    [InlineData(1000000, "1,000,000")]
    [InlineData(0, "0")]
    public void FormatNumber_TrimsTrailingZerosAndGroupsThousands(double value, string expected) =>
        Assert.Equal(expected, ColumnStatistics.FormatNumber(value));
}
