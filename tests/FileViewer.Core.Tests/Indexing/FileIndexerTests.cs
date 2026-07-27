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
    public async Task BadColumnCount_RowsAreStillIndexedWithoutAnyDiagnostics()
    {
        // Explicit product decision: a row's field count is never validated against the header's
        // declared column count. Rows with too few/too many fields are indexed and shown exactly
        // like any other row, with no warning generated.
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("bad_column_count.dif"));

        Assert.True(index.Header.IsValid);
        Assert.Equal((nuint)3, index.RowIndex.Count);
        Assert.Empty(index.Diagnostics);
    }

    [Fact]
    public async Task DataRecordsMismatch_IndexesActualRowCountWithoutAnyDiagnostics()
    {
        // Same principle for the trailer's DATARECORDS count: it's parsed into
        // DifFileHeader.DeclaredDataRecords for informational purposes, but a mismatch against the
        // actual indexed row count is never flagged.
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("datarecords_mismatch.dif"));

        Assert.True(index.Header.IsValid);
        Assert.Equal((nuint)3, index.RowIndex.Count);
        Assert.Equal(5, index.Header.DeclaredDataRecords);
        Assert.Empty(index.Diagnostics);
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
    public async Task RealSampleFile_IndexesAllFiftyRowsDespiteColumnCountMismatch()
    {
        // Every row in this real sample actually has 33 fields against a 30-column header (see
        // DifHeaderParserTests) — confirms that mismatch has zero effect on indexing: no
        // diagnostics, every row present, content and order intact.
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("FixedIncomeAsia.dif"));

        Assert.True(index.Header.IsValid);
        Assert.Equal((nuint)50, index.RowIndex.Count);
        Assert.Equal((nuint)50, index.SortKeys.Count);
        Assert.Empty(index.Diagnostics);

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

    [Fact]
    public async Task GetRowBytes_IsSafeUnderConcurrentReads()
    {
        // FileIndex no longer holds one persistent whole-file mapping — every GetRowBytes call now
        // does its own RandomAccess.Read. This is the property the rest of the app (grid rendering
        // on the UI thread, export/sort/filter on background threads) relies on being safe to call
        // from multiple threads at once against the same FileIndex.
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("FixedIncomeAsia.dif"));

        var tasks = new Task[50];
        for (int t = 0; t < tasks.Length; t++)
        {
            tasks[t] = Task.Run(() =>
            {
                for (int i = 0; i < 50; i++)
                {
                    int rowIndex = i % (int)index.RowIndex.Count;
                    string[] fields = DifRowParser.ParseRow(index.GetRowBytes(rowIndex), (byte)index.Header.Delimiter, DifFormatOptions.TextEncoding);
                    Assert.Equal($"SEC{1000 + rowIndex:D4} HK Equity", fields[0]);
                }
            });
        }

        await Task.WhenAll(tasks);
    }

    [Theory]
    [InlineData(0, 1)] // empty data region — always exactly one (no-op) chunk
    [InlineData(1024, 1)] // tiny file — well under both the core-count and max-chunk-size thresholds
    [InlineData(FileIndexer.MaxChunkReadBytes * 3, 3)] // no cores would ever need to be involved for the cap alone to force multiple chunks
    public void ComputeChunkCount_NeverLetsAnySingleChunkExceedTheMaxReadSize(long dataLength, int minimumExpectedChunks)
    {
        // The core-count heuristic alone would produce very few, very large chunks for a huge file
        // on a low-core machine — this is what actually bounds peak Phase B memory regardless of
        // file size or Environment.ProcessorCount.
        int chunkCount = FileIndexer.ComputeChunkCount(dataLength);

        Assert.True(chunkCount >= minimumExpectedChunks,
            $"Expected at least {minimumExpectedChunks} chunk(s) for a {dataLength}-byte data region, got {chunkCount}.");
        if (dataLength > 0)
        {
            long largestPossibleChunk = (dataLength + chunkCount - 1) / chunkCount;
            Assert.True(largestPossibleChunk <= FileIndexer.MaxChunkReadBytes,
                $"A {dataLength}-byte data region split into {chunkCount} chunks could still produce a chunk up to {largestPossibleChunk} bytes, exceeding MaxChunkReadBytes.");
        }
    }
}
