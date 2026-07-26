using FileViewer.Core.Dif;
using FileViewer.Core.Tests.TestSupport;

namespace FileViewer.Core.Tests.Dif;

public class DifHeaderParserTests
{
    private static int CountDataRows(ReadOnlySpan<byte> content, DifFileHeader header)
    {
        int pos = checked((int)header.DataStartOffset);
        int end = checked((int)header.DataEndOffsetExclusive);
        int count = 0;
        while (pos < end && DifLineScanner.TryReadLine(content[..end], ref pos, out ReadOnlySpan<byte> line))
        {
            if (DifLineScanner.TrimTrailingCr(line).Length > 0) count++;
        }
        return count;
    }

    [Fact]
    public void MinimalValid_ParsesHeaderFieldsAndTrailerCorrectly()
    {
        byte[] content = FixtureLoader.ReadBytes("minimal_valid.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal('|', header.Delimiter);
        Assert.Equal(["_ID", "_ERR", "PRICE"], header.ColumnNames);
        Assert.Equal("testfirm", header.HeaderMetadata["FIRMNAME"]);
        Assert.Equal(3, header.DeclaredDataRecords);
        Assert.Equal(3, CountDataRows(content, header));
        Assert.DoesNotContain(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
    }

    [Fact]
    public void EmptyData_ZeroLengthDataRegionAndZeroDeclaredRecords()
    {
        byte[] content = FixtureLoader.ReadBytes("empty_data.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal(0, header.DeclaredDataRecords);
        Assert.Equal(header.DataStartOffset, header.DataEndOffsetExclusive);
        Assert.Equal(0, CountDataRows(content, header));
    }

    [Fact]
    public void TruncatedMidRow_StillOpens_FallsBackDataEndToEofWithWarnings()
    {
        byte[] content = FixtureLoader.ReadBytes("truncated_mid_row.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal(content.Length, header.DataEndOffsetExclusive);
        Assert.Contains(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Warning && d.Message.Contains(DifFormatOptions.DataEnd));
        Assert.Contains(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Warning && d.Message.Contains(DifFormatOptions.Trailer));
    }

    [Fact]
    public void TruncatedBeforeTrailer_DataEndFound_TrailerMissingIsWarningOnly()
    {
        byte[] content = FixtureLoader.ReadBytes("truncated_before_trailer.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal(3, CountDataRows(content, header));
        Assert.Null(header.DeclaredDataRecords);
        Assert.Contains(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Warning && d.Message.Contains(DifFormatOptions.Trailer));
    }

    [Fact]
    public void MissingEndOfFields_IsInvalid()
    {
        byte[] content = FixtureLoader.ReadBytes("missing_end_of_fields.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.False(header.IsValid);
        Assert.Contains(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
    }

    [Fact]
    public void MissingEndOfData_StillOpens_FallsBackDataEndToEof()
    {
        byte[] content = FixtureLoader.ReadBytes("missing_end_of_data.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal(content.Length, header.DataEndOffsetExclusive);
        Assert.Equal(3, CountDataRows(content, header));
    }

    [Fact]
    public void BadColumnCount_HeaderStillParses_PerRowFieldCountsExposeTheMismatch()
    {
        byte[] content = FixtureLoader.ReadBytes("bad_column_count.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);
        Assert.True(header.IsValid);
        Assert.Equal(3, header.ColumnNames.Count);

        int pos = checked((int)header.DataStartOffset);
        int end = checked((int)header.DataEndOffsetExclusive);
        var fieldCounts = new List<int>();
        while (pos < end && DifLineScanner.TryReadLine(content.AsSpan()[..end], ref pos, out ReadOnlySpan<byte> line))
        {
            if (DifLineScanner.TrimTrailingCr(line).Length == 0) continue;
            fieldCounts.Add(DifRowParser.CountFields(line, (byte)header.Delimiter));
        }

        // Rows deliberately have 3, 2, and 4 fields against a 3-column header — the indexer (not
        // this parser) is responsible for turning these mismatches into per-row diagnostics.
        Assert.Equal([3, 2, 4], fieldCounts);
    }

    [Fact]
    public void DataRecordsMismatch_DeclaredCountDoesNotHaveToMatchActualRows()
    {
        byte[] content = FixtureLoader.ReadBytes("datarecords_mismatch.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal(5, header.DeclaredDataRecords);
        Assert.Equal(3, CountDataRows(content, header));
    }

    [Fact]
    public void MissingDelimiterKey_DefaultsToPipeWithWarning()
    {
        byte[] content = FixtureLoader.ReadBytes("missing_delimiter_key.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal(DifFormatOptions.DefaultDelimiter, header.Delimiter);
        Assert.Contains(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Warning && d.Message.Contains(DifFormatOptions.DelimiterKey));
    }

    [Fact]
    public void CrlfAndLfMixed_ParsesIdenticallyToLfOnly()
    {
        byte[] content = FixtureLoader.ReadBytes("crlf_and_lf_mixed.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal(["_ID", "_ERR", "PRICE"], header.ColumnNames);
        Assert.Equal('|', header.Delimiter);
        Assert.Equal(3, header.DeclaredDataRecords);
        Assert.Equal(3, CountDataRows(content, header));
    }

    [Fact]
    public void NotADifFile_IsInvalidWithClearDiagnostic()
    {
        byte[] content = FixtureLoader.ReadBytes("not_a_dif_file.bin");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.False(header.IsValid);
        Assert.Contains(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error && d.Message.Contains(DifFormatOptions.HeaderStart));
    }

    [Fact]
    public void RealSampleFile_ParsesFullGrammarEndToEnd()
    {
        byte[] content = FixtureLoader.ReadBytes("FixedIncomeAsia.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal('|', header.Delimiter);
        Assert.Equal(30, header.ColumnNames.Count);
        Assert.Equal("_ID", header.ColumnNames[0]);
        Assert.Equal("DAY_COUNT_CONVENTION", header.ColumnNames[^1]);
        Assert.Equal(50, header.DeclaredDataRecords);
        Assert.Equal(50, CountDataRows(content, header));
        Assert.DoesNotContain(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
    }

    [Fact]
    public void RealSampleFile_DataRowsHaveMoreFieldsThanDeclaredColumns()
    {
        // The real sample export declares 30 columns (START-OF-FIELDS..END-OF-FIELDS) but every
        // data row actually carries 33 pipe-delimited fields — a genuine column-count mismatch in
        // real-world mock data, not a fixture bug. DifHeaderParser/DifRowParser only expose the raw
        // counts; turning this into a per-row diagnostic instead of a crash is FileIndexer's job
        // (built in a later pass), exercised here to confirm the building blocks it needs are correct.
        byte[] content = FixtureLoader.ReadBytes("FixedIncomeAsia.dif");
        DifFileHeader header = DifHeaderParser.Parse(content);

        int pos = checked((int)header.DataStartOffset);
        int end = checked((int)header.DataEndOffsetExclusive);
        Assert.True(DifLineScanner.TryReadLine(content.AsSpan()[..end], ref pos, out ReadOnlySpan<byte> firstDataLine));

        int actualFieldCount = DifRowParser.CountFields(firstDataLine, (byte)header.Delimiter);

        Assert.Equal(33, actualFieldCount);
        Assert.NotEqual(header.ColumnNames.Count, actualFieldCount);
    }
}
