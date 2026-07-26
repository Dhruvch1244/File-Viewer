using System.Text;
using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;

namespace FileViewer.Core.Tests.Indexing;

public class FileIndexerTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    [Fact]
    public async Task MinimalValid_IndexesAllRowsInFileOrderWithCorrectBytes()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));

        Assert.True(index.Header.IsValid);
        Assert.Equal((nuint)3, index.RowIndex.Count);

        string[] expectedIds = ["SEC001 HK Equity", "SEC002 HK Equity", "SEC003 HK Equity"];
        for (int i = 0; i < 3; i++)
        {
            ReadOnlySpan<byte> rowBytes = index.GetRowBytes(i);
            string[] fields = DifRowParser.ParseRow(rowBytes, (byte)index.Header.Delimiter, DifFormatOptions.TextEncoding);
            Assert.Equal(expectedIds[i], fields[0]);
        }
    }

    [Fact]
    public async Task MinimalValid_SortKeysMatchRowIdsInFileOrder()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));

        Assert.Equal((nuint)3, index.SortKeys.Count);
        string[] expectedIds = ["SEC001 HK Equity", "SEC002 HK Equity", "SEC003 HK Equity"];
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(i, index.SortKeys[i].RowIndex);
            Assert.Equal(expectedIds[i], Encoding.ASCII.GetString(index.SortKeys[i].GetKeySpan()));
        }
    }

    [Fact]
    public async Task EmptyData_ProducesZeroRowsWithoutError()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("empty_data.dif"));

        Assert.True(index.Header.IsValid);
        Assert.Equal((nuint)0, index.RowIndex.Count);
        Assert.DoesNotContain(index.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task TruncatedMidRow_StillIndexesCompleteRowsGracefully()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("truncated_mid_row.dif"));

        Assert.True(index.Header.IsValid);
        // Two full rows plus the trailing partial row (no terminator) are all still indexed —
        // the indexer never crashes on truncated input (PRS §8 Reliability).
        Assert.Equal((nuint)3, index.RowIndex.Count);
        Assert.DoesNotContain(index.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task MissingEndOfFields_ProducesInvalidIndexWithNoRows()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("missing_end_of_fields.dif"));

        Assert.False(index.Header.IsValid);
        Assert.Equal((nuint)0, index.RowIndex.Count);
        Assert.Contains(index.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task NotADifFile_ProducesInvalidIndexWithoutThrowing()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("not_a_dif_file.bin"));

        Assert.False(index.Header.IsValid);
        Assert.Equal((nuint)0, index.RowIndex.Count);
    }

    [Fact]
    public async Task BadColumnCount_RowsAreStillIndexedWithPerRowDiagnostics()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("bad_column_count.dif"));

        Assert.True(index.Header.IsValid);
        Assert.Equal((nuint)3, index.RowIndex.Count);

        var warnings = index.Diagnostics.Where(d => d.Severity == DifDiagnosticSeverity.Warning && d.Message.Contains("field(s)")).ToList();
        Assert.Equal(2, warnings.Count); // rows with 2 and 4 fields against a 3-column header; the 3-field row is fine
    }

    [Fact]
    public async Task DataRecordsMismatch_ProducesWarningNotError()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("datarecords_mismatch.dif"));

        Assert.True(index.Header.IsValid);
        Assert.Equal((nuint)3, index.RowIndex.Count);
        Assert.Contains(index.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Warning && d.Message.Contains("DATARECORDS"));
        Assert.DoesNotContain(index.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
    }

    [Fact]
    public async Task CrlfAndLfMixed_IndexesIdenticallyToLfOnly()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("crlf_and_lf_mixed.dif"));

        Assert.True(index.Header.IsValid);
        Assert.Equal((nuint)3, index.RowIndex.Count);
        ReadOnlySpan<byte> firstRow = index.GetRowBytes(0);
        Assert.Equal("SEC001 HK Equity", DifRowParser.ParseRow(firstRow, (byte)'|', DifFormatOptions.TextEncoding)[0]);
    }

    [Fact]
    public async Task RealSampleFile_IndexesAllFiftyRowsWithConsistentColumnMismatchDiagnostics()
    {
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("FixedIncomeAsia.dif"));

        Assert.True(index.Header.IsValid);
        Assert.Equal((nuint)50, index.RowIndex.Count);
        Assert.Equal((nuint)50, index.SortKeys.Count);
        Assert.DoesNotContain(index.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);

        // Every row has 33 fields against the 30-column header (see DifHeaderParserTests) — the
        // indexer should have recorded exactly one mismatch warning per data row.
        int mismatchWarnings = index.Diagnostics.Count(d => d.Severity == DifDiagnosticSeverity.Warning && d.Message.Contains("field(s)"));
        Assert.Equal(50, mismatchWarnings);

        // Spot-check row order and content is preserved.
        string[] firstRow = DifRowParser.ParseRow(index.GetRowBytes(0), (byte)index.Header.Delimiter, DifFormatOptions.TextEncoding);
        string[] lastRow = DifRowParser.ParseRow(index.GetRowBytes(49), (byte)index.Header.Delimiter, DifFormatOptions.TextEncoding);
        Assert.Equal("SEC1000 HK Equity", firstRow[0]);
        Assert.Equal("SEC1049 HK Equity", lastRow[0]);

        Assert.Equal(0, index.SortKeys[0].RowIndex);
        Assert.Equal(49, index.SortKeys[49].RowIndex);
    }

    [Fact]
    public async Task Cancellation_ThrowsOperationCanceledException()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => FileIndexer.IndexAsync(FixturePath("FixedIncomeAsia.dif"), cancellationToken: cts.Token));
    }
}
