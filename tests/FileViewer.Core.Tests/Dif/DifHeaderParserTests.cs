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
    public void ImahdrGetdataFile_WithImplicitIdErrSizePrefix_ParsesAndSynthesizesLeadingColumns()
    {
        // Mirrors a real Bloomberg Data License "getdata" export: header marker is IMAHDR (not
        // INAHDR), a START-OF-FILE marker line precedes the KEY=VALUE metadata, a TIMESTARTED=...
        // line sits between END-OF-FIELDS and START-OF-DATA, and every data row carries an implicit
        // security-ID|error-code|field-count triplet ahead of the declared TICKER/CPN/MATURITY
        // values without those three being declared in START-OF-FIELDS.
        byte[] content = FixtureLoader.ReadBytes("bloomberg_getdata_implicit_prefix.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.DoesNotContain(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
        Assert.Equal(DifFormatOptions.HeaderStartAlt, header.HeaderMarker);
        Assert.True(header.HasFileStartMarker);
        Assert.Equal(["_ID", "_ERR", "_SIZE", "TICKER", "CPN", "MATURITY"], header.ColumnNames);
        Assert.Equal("getdata", header.HeaderMetadata["PROGRAMNAME"]);
        Assert.False(header.HeaderMetadata.ContainsKey("TIMESTARTED")); // belongs after END-OF-FIELDS, not the header preamble
        Assert.Equal("Thu Jul 23 18:30:54 EDT 2026", header.PostFieldsMetadata["TIMESTARTED"]);
        Assert.Equal(3, header.DeclaredDataRecords);
        Assert.Equal(3, CountDataRows(content, header));

        int pos = checked((int)header.DataStartOffset);
        int end = checked((int)header.DataEndOffsetExclusive);
        Assert.True(DifLineScanner.TryReadLine(content.AsSpan()[..end], ref pos, out ReadOnlySpan<byte> firstDataLine));
        string[] fields = DifRowParser.ParseRow(firstDataLine, (byte)header.Delimiter, DifFormatOptions.TextEncoding);

        // With the synthesized leading columns, every column index now lines up with its row's
        // actual field index — TICKER (column 3) reads "PRETSL", not the security ID.
        Assert.Equal("EC3478608 Corp", fields[header.ColumnIndexOf("_ID")]);
        Assert.Equal("0", fields[header.ColumnIndexOf("_ERR")]);
        Assert.Equal("3", fields[header.ColumnIndexOf("_SIZE")]);
        Assert.Equal("PRETSL", fields[header.ColumnIndexOf("TICKER")]);
        Assert.Equal("9.550000", fields[header.ColumnIndexOf("CPN")]);
        Assert.Equal("20310301", fields[header.ColumnIndexOf("MATURITY")]);
    }

    [Fact]
    public void HeaderMarkerLine_WithTrailingPipeDelimitedContent_StillOpens()
    {
        // Some real-world exports pack extra fields onto the marker line itself instead of the
        // marker standing alone — e.g. "IMAHDR|BBGB0525-20260723-...|mfts-bwc|". Previously this
        // failed the exact-line-match check entirely, so the file never opened at all (it isn't a
        // column-alignment issue like the earlier fixes — the header parse itself was rejecting
        // the file before ever reaching START-OF-FIELDS).
        byte[] content = FixtureLoader.ReadBytes("marker_line_with_trailing_content.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.DoesNotContain(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
        Assert.Equal(DifFormatOptions.HeaderStartAlt, header.HeaderMarker);
        Assert.Equal(["_ID", "_ERR", "_SIZE", "TICKER", "CPN", "MATURITY"], header.ColumnNames);
        Assert.Equal(3, CountDataRows(content, header));
    }

    [Fact]
    public void ImplicitPrefix_IsAppliedUnconditionally_RegardlessOfRowShapeOrFileSize()
    {
        // The security-ID|error-code|field-count triplet is a fixed property of the wire format,
        // not something to infer from row contents — a small file (few rows, one of them short a
        // trailing optional field) must get the same offset as a large, uniform one. This fixture's
        // first row is missing a trailing field on purpose to prove the offset no longer depends on
        // any row's actual shape.
        byte[] content = FixtureLoader.ReadBytes("futures_reference_implicit_prefix.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.DoesNotContain(header.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
        Assert.Equal(
            ["_ID", "_ERR", "_SIZE", "TICKER", "EXCH_CODE", "ID_BB_GLOBAL", "UNIQUE_ID_FUT_OPT", "PARSEKYABLE_DES_SOURCE"],
            header.ColumnNames);

        int pos = checked((int)header.DataStartOffset);
        int end = checked((int)header.DataEndOffsetExclusive);
        Assert.True(DifLineScanner.TryReadLine(content.AsSpan()[..end], ref pos, out _)); // skip the short first row
        Assert.True(DifLineScanner.TryReadLine(content.AsSpan()[..end], ref pos, out ReadOnlySpan<byte> secondDataLine));
        string[] fields = DifRowParser.ParseRow(secondDataLine, (byte)header.Delimiter, DifFormatOptions.TextEncoding);

        Assert.Equal("AKRM7 Index", fields[header.ColumnIndexOf("_ID")]);
        Assert.Equal("70", fields[header.ColumnIndexOf("_SIZE")]);
        Assert.Equal("AKRM7", fields[header.ColumnIndexOf("TICKER")]);
        Assert.Equal("KFE", fields[header.ColumnIndexOf("EXCH_CODE")]);
        Assert.Equal("BBG022YS3M48", fields[header.ColumnIndexOf("ID_BB_GLOBAL")]);
        Assert.Equal("AKRM7", fields[header.ColumnIndexOf("UNIQUE_ID_FUT_OPT")]);
        Assert.Equal("Comdty", fields[header.ColumnIndexOf("PARSEKYABLE_DES_SOURCE")]);
    }

    [Fact]
    public void ImplicitPrefix_IsAppliedEvenWithZeroDataRows_NothingToPeekAt()
    {
        // No row-shape peek is possible here at all (empty data section) — the offset must still
        // apply, because it's a property of the format/columns declared, not of any row's contents.
        byte[] content = FixtureLoader.ReadBytes("getdata_empty_data_implicit_prefix.dif");

        DifFileHeader header = DifHeaderParser.Parse(content);

        Assert.True(header.IsValid);
        Assert.Equal(["_ID", "_ERR", "_SIZE", "TICKER", "CPN", "MATURITY"], header.ColumnNames);
        Assert.Equal(0, header.DeclaredDataRecords);
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
