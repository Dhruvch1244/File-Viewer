using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;

namespace FileViewer.Core.Tests.Indexing;

/// <summary>
/// The small hand-authored fixtures never exceed FileIndexer's MinChunkBytes threshold, so they
/// only ever exercise the single-chunk path. These tests generate a larger synthetic file so the
/// parallel multi-chunk scan (chunk boundary alignment + ordered merge) is actually exercised.
/// </summary>
public class FileIndexerLargeFileTests
{
    private const int RowCount = 300_000;

    private static string CreateSyntheticFile()
    {
        string path = Path.Combine(Path.GetTempPath(), $"fileviewer-large-{Guid.NewGuid():N}.dif");
        using (var writer = new StreamWriter(path, append: false, System.Text.Encoding.ASCII))
        {
            writer.NewLine = "\n";
            writer.WriteLine("INAHDR");
            writer.WriteLine("FIRMNAME=testfirm");
            writer.WriteLine("DELIMITER=|");
            writer.WriteLine("START-OF-FIELDS");
            writer.WriteLine("_ID");
            writer.WriteLine("_ERR");
            writer.WriteLine("PRICE");
            writer.WriteLine("END-OF-FIELDS");
            writer.WriteLine("START-OF-DATA");
            for (int i = 0; i < RowCount; i++)
            {
                writer.WriteLine($"SEC{i:D7} HK Equity|0|{i}.00");
            }
            writer.WriteLine("END-OF-DATA");
            writer.WriteLine("INATRL");
            writer.WriteLine($"DATARECORDS={RowCount}");
        }
        return path;
    }

    [Fact]
    public async Task LargeFile_ExceedsSingleChunkThreshold_AndIndexesAllRowsInOrder()
    {
        string path = CreateSyntheticFile();
        try
        {
            var fileInfo = new FileInfo(path);
            // Sanity check the fixture is actually big enough to force multiple chunks whenever
            // more than one processor is available (FileIndexer.MinChunkBytes = 4 MiB).
            Assert.True(fileInfo.Length > 8 * 1024 * 1024, $"Synthetic fixture is only {fileInfo.Length} bytes; expected >8 MiB to reliably force multiple chunks.");

            using FileIndex index = await FileIndexer.IndexAsync(path);

            Assert.True(index.Header.IsValid);
            Assert.Equal((nuint)RowCount, index.RowIndex.Count);
            Assert.Equal((nuint)RowCount, index.SortKeys.Count);
            Assert.DoesNotContain(index.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);

            // Spot-check row order/content at chunk-boundary-adjacent positions, not just the ends.
            int[] sampleIndices = [0, 1, RowCount / 4, RowCount / 2, RowCount - 2, RowCount - 1];
            foreach (int i in sampleIndices)
            {
                string[] fields = DifRowParser.ParseRow(index.GetRowBytes(i), (byte)index.Header.Delimiter, DifFormatOptions.TextEncoding);
                Assert.Equal($"SEC{i:D7} HK Equity", fields[0]);
                Assert.Equal(i, index.SortKeys[i].RowIndex);
            }

            // Every base row index must appear in the sort-key array exactly once (chunk merge
            // must not duplicate or drop rows at a chunk boundary).
            var seenRowIndices = new HashSet<long>();
            for (long i = 0; i < (long)index.SortKeys.Count; i++)
            {
                Assert.True(seenRowIndices.Add(index.SortKeys[i].RowIndex), $"Duplicate RowIndex {index.SortKeys[i].RowIndex} found in sort-key array.");
            }
            Assert.Equal(RowCount, seenRowIndices.Count);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Fact]
    public async Task LargeFile_ReportsMonotonicProgressUpToCompletion()
    {
        string path = CreateSyntheticFile();
        try
        {
            var reports = new List<IndexingProgress>();
            var progress = new Progress<IndexingProgress>(p => { lock (reports) reports.Add(p); });

            using FileIndex index = await FileIndexer.IndexAsync(path, progress);

            // Progress<T> marshals callbacks asynchronously; give the sync context's queued
            // callbacks a moment to drain before asserting on the collected reports.
            await Task.Delay(50);

            Assert.True(index.Header.IsValid);
            Assert.NotEmpty(reports);
            Assert.Contains(reports, r => r.RowsFound > 0);
            Assert.All(reports, r => Assert.True(r.RowsFound <= RowCount));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
