using System.Text;
using System.Text.Json;
using FileViewer.Core.Caching;
using FileViewer.Core.Dif;
using FileViewer.Core.Export;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Tests.Export;

public class ExportRoundTripTests
{
    private static string FixturePath(string fileName) => Path.Combine(AppContext.BaseDirectory, "Fixtures", fileName);

    private sealed record Scenario(FileIndex Index, EditOverlay Overlay, DecodedRowCache Cache, long AddedRowIndex) : IDisposable
    {
        public void Dispose() => Index.Dispose();
    }

    /// <summary>
    /// minimal_valid.dif has 3 rows (SEC001/SEC002/SEC003). This scenario edits row 1's PRICE,
    /// deletes row 2, and adds a new row — the same mixed overlay state every format is exercised
    /// against, so results are directly comparable across exporters.
    /// </summary>
    private static async Task<Scenario> BuildMixedOverlayScenarioAsync()
    {
        FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();

        overlay.EditCell(1, "PRICE", "555.55");
        overlay.DeleteRow(2);
        long addedIndex = overlay.AddRow(index.Header.ColumnNames);
        overlay.EditCell(addedIndex, "_ID", "NEWSEC");

        return new Scenario(index, overlay, cache, addedIndex);
    }

    [Fact]
    public async Task Dif_RoundTrip_ReparsesToExpectedResolvedRows()
    {
        using Scenario scenario = await BuildMixedOverlayScenarioAsync();
        using var stream = new MemoryStream();

        ExportRunner.Export(stream, scenario.Index, scenario.Overlay, scenario.Cache,
            ExportRunner.FileOrderWithAddedRows(scenario.Index, scenario.Overlay),
            new DifExporter(scenario.Index));

        byte[] exportedBytes = stream.ToArray();
        DifFileHeader reparsedHeader = DifHeaderParser.Parse(exportedBytes);

        Assert.True(reparsedHeader.IsValid);
        // Trailer is copied verbatim from minimal_valid.dif, which declares DATARECORDS=3 — same
        // number as this scenario happens to write (row0 + edited row1 + added row, row2 deleted).
        Assert.Equal(3, reparsedHeader.DeclaredDataRecords);

        var rows = new List<string[]>();
        int pos = checked((int)reparsedHeader.DataStartOffset);
        int end = checked((int)reparsedHeader.DataEndOffsetExclusive);
        while (pos < end && DifLineScanner.TryReadLine(exportedBytes.AsSpan()[..end], ref pos, out ReadOnlySpan<byte> line))
        {
            if (DifLineScanner.TrimTrailingCr(line).Length == 0) continue;
            rows.Add(DifRowParser.ParseRow(line, (byte)reparsedHeader.Delimiter, DifFormatOptions.TextEncoding));
        }

        Assert.Equal(3, rows.Count);
        Assert.Equal(["SEC001 HK Equity", "0", "100.50"], rows[0]);
        Assert.Equal(["SEC002 HK Equity", "0", "555.55"], rows[1]);
        Assert.Equal(["NEWSEC", "", ""], rows[2]);
    }

    [Fact]
    public async Task Dif_RoundTrip_PreservesEverythingOutsideTheDataSection()
    {
        // bloomberg_getdata_implicit_prefix.dif carries exactly the "other stuff" that isn't a row
        // value: an IMAHDR marker (not INAHDR), a START-OF-FILE line, pre-fields metadata
        // (PROGRAMNAME/DATEFORMAT/ENCODING), post-fields metadata (TIMESTARTED, sitting between
        // END-OF-FIELDS and START-OF-DATA), and a trailer with a second key (ENDTIME) beyond
        // DATARECORDS. None of that is a column the grid displays, so it only survives an export if
        // DifExporter is copying those bytes straight from the source file — this proves it does.
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("bloomberg_getdata_implicit_prefix.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();
        using var stream = new MemoryStream();

        ExportRunner.Export(stream, index, overlay, cache,
            ExportRunner.FileOrderWithAddedRows(index, overlay),
            new DifExporter(index));

        byte[] exportedBytes = stream.ToArray();
        DifFileHeader reparsedHeader = DifHeaderParser.Parse(exportedBytes);

        Assert.True(reparsedHeader.IsValid);
        Assert.Equal(DifFormatOptions.HeaderStartAlt, reparsedHeader.HeaderMarker);
        Assert.True(reparsedHeader.HasFileStartMarker);
        Assert.Equal("getdata", reparsedHeader.HeaderMetadata["PROGRAMNAME"]);
        Assert.Equal("yyyymmdd", reparsedHeader.HeaderMetadata["DATEFORMAT"]);
        Assert.Equal("UTF-8", reparsedHeader.HeaderMetadata["ENCODING"]);
        Assert.Equal("Thu Jul 23 18:30:54 EDT 2026", reparsedHeader.PostFieldsMetadata["TIMESTARTED"]);
        Assert.Equal("Thu Jul 23 18:31:02 EDT 2026", reparsedHeader.TrailerMetadata["ENDTIME"]);
        Assert.Equal(3, reparsedHeader.DeclaredDataRecords); // copied verbatim from the source file's own DATARECORDS=3
        Assert.Equal(DifFormatOptions.Trailer, reparsedHeader.TrailerMarker); // this fixture's trailer is plain INATRL, no END-OF-FILE
        Assert.False(reparsedHeader.HasFileEndMarker);
    }

    [Fact]
    public async Task Dif_RoundTrip_PreservesImatrlTrailerMarkerAndEndOfFileLine()
    {
        // real_world_trailer_before_end_of_file.dif uses the OTHER real trailer shape: IMATRL (not
        // INATRL) as the closing marker, with an END-OF-FILE line right before it — both must
        // survive a re-export the same way the header-side IMAHDR/START-OF-FILE pair already does.
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("real_world_trailer_before_end_of_file.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();
        using var stream = new MemoryStream();

        ExportRunner.Export(stream, index, overlay, cache,
            ExportRunner.FileOrderWithAddedRows(index, overlay),
            new DifExporter(index));

        byte[] exportedBytes = stream.ToArray();
        DifFileHeader reparsedHeader = DifHeaderParser.Parse(exportedBytes);

        Assert.True(reparsedHeader.IsValid);
        Assert.Equal(DifFormatOptions.TrailerAlt, reparsedHeader.TrailerMarker);
        Assert.True(reparsedHeader.HasFileEndMarker);
        Assert.Equal("Thu Jul 23 19:08:48 EDT 2026", reparsedHeader.TrailerMetadata["TIMEFINISHED"]);
        // This fixture's DATARECORDS=22617 is already stale against its own 3 data rows — copied
        // verbatim rather than "corrected", exactly as a since-edited file's trailer would be.
        Assert.Equal(22617, reparsedHeader.DeclaredDataRecords);
    }

    [Fact]
    public async Task Dif_RoundTrip_PreservesDataRecordsVerbatimEvenWhenEditsMakeItStale()
    {
        // minimal_valid.dif declares DATARECORDS=3. Deleting two of its three rows means only one
        // row is actually written back out — but the trailer is copied byte-for-byte from the
        // source file, so the now-stale DATARECORDS=3 must survive untouched, not get "corrected"
        // to 1. Only the bytes between START-OF-DATA and END-OF-DATA are ever regenerated.
        using FileIndex index = await FileIndexer.IndexAsync(FixturePath("minimal_valid.dif"));
        var overlay = new EditOverlay();
        var cache = new DecodedRowCache();
        overlay.DeleteRow(1);
        overlay.DeleteRow(2);
        using var stream = new MemoryStream();

        ExportRunner.Export(stream, index, overlay, cache,
            ExportRunner.FileOrderWithAddedRows(index, overlay),
            new DifExporter(index));

        byte[] exportedBytes = stream.ToArray();
        DifFileHeader reparsedHeader = DifHeaderParser.Parse(exportedBytes);

        Assert.True(reparsedHeader.IsValid);
        Assert.Equal(3, reparsedHeader.DeclaredDataRecords);

        var rows = new List<string[]>();
        int pos = checked((int)reparsedHeader.DataStartOffset);
        int end = checked((int)reparsedHeader.DataEndOffsetExclusive);
        while (pos < end && DifLineScanner.TryReadLine(exportedBytes.AsSpan()[..end], ref pos, out ReadOnlySpan<byte> line))
        {
            if (DifLineScanner.TrimTrailingCr(line).Length == 0) continue;
            rows.Add(DifRowParser.ParseRow(line, (byte)reparsedHeader.Delimiter, DifFormatOptions.TextEncoding));
        }

        Assert.Single(rows); // only row0 survives the two deletes, despite DATARECORDS still reading 3
    }

    [Fact]
    public async Task Csv_RoundTrip_QuotesFieldsContainingCommaOrQuote()
    {
        using Scenario scenario = await BuildMixedOverlayScenarioAsync();
        scenario.Overlay.EditCell(0, "_ID", "SEC001, \"Special\" Equity");
        using var stream = new MemoryStream();

        ExportRunner.Export(stream, scenario.Index, scenario.Overlay, scenario.Cache,
            ExportRunner.FileOrderWithAddedRows(scenario.Index, scenario.Overlay),
            new CsvExporter());

        string csv = Encoding.UTF8.GetString(stream.ToArray());
        string[] lines = csv.Split("\r\n", StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("_ID,_ERR,PRICE", lines[0]);
        Assert.Equal("\"SEC001, \"\"Special\"\" Equity\",0,100.50", lines[1]);
        Assert.DoesNotContain(lines, l => l.StartsWith("SEC003", StringComparison.Ordinal)); // deleted row never appears
        Assert.Equal(4, lines.Length); // header + row0(edited) + row1(edited) + added row
    }

    [Fact]
    public async Task Tsv_RoundTrip_SanitizesEmbeddedTabsAndNewlines()
    {
        using Scenario scenario = await BuildMixedOverlayScenarioAsync();
        scenario.Overlay.EditCell(0, "_ID", "SEC001\tWith\nEmbedded");
        using var stream = new MemoryStream();

        ExportRunner.Export(stream, scenario.Index, scenario.Overlay, scenario.Cache,
            ExportRunner.FileOrderWithAddedRows(scenario.Index, scenario.Overlay),
            new TsvExporter());

        string tsv = Encoding.UTF8.GetString(stream.ToArray());
        string[] lines = tsv.Split('\n', StringSplitOptions.RemoveEmptyEntries);

        Assert.Equal("_ID\t_ERR\tPRICE", lines[0]);
        Assert.Equal("SEC001 With Embedded\t0\t100.50", lines[1]);
        Assert.DoesNotContain(lines, l => l.StartsWith("SEC003", StringComparison.Ordinal));
        Assert.Equal(4, lines.Length);
    }

    [Fact]
    public async Task Json_RoundTrip_ProducesArrayOfObjectsKeyedByColumnName()
    {
        using Scenario scenario = await BuildMixedOverlayScenarioAsync();
        using var stream = new MemoryStream();

        ExportRunner.Export(stream, scenario.Index, scenario.Overlay, scenario.Cache,
            ExportRunner.FileOrderWithAddedRows(scenario.Index, scenario.Overlay),
            new JsonExporter());

        using JsonDocument document = JsonDocument.Parse(stream.ToArray());
        JsonElement.ArrayEnumerator rows = document.RootElement.EnumerateArray();
        var materialized = rows.ToList();

        Assert.Equal(3, materialized.Count);
        Assert.Equal("SEC001 HK Equity", materialized[0].GetProperty("_ID").GetString());
        Assert.Equal("555.55", materialized[1].GetProperty("PRICE").GetString());
        Assert.Equal("NEWSEC", materialized[2].GetProperty("_ID").GetString());
        Assert.DoesNotContain(materialized, row => row.GetProperty("_ID").GetString()!.StartsWith("SEC003", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData(typeof(DifExporter))]
    [InlineData(typeof(CsvExporter))]
    [InlineData(typeof(TsvExporter))]
    [InlineData(typeof(JsonExporter))]
    public async Task AllFormats_NeverEmitDeletedRows(Type exporterType)
    {
        using Scenario scenario = await BuildMixedOverlayScenarioAsync();
        using var stream = new MemoryStream();
        IRowExporter exporter = exporterType == typeof(DifExporter)
            ? new DifExporter(scenario.Index)
            : (IRowExporter)Activator.CreateInstance(exporterType)!;

        ExportRunner.Export(stream, scenario.Index, scenario.Overlay, scenario.Cache,
            ExportRunner.FileOrderWithAddedRows(scenario.Index, scenario.Overlay), exporter);

        string output = Encoding.UTF8.GetString(stream.ToArray());
        Assert.DoesNotContain("SEC003", output);
        exporter.Dispose();
    }
}
