using System.Text;
using FileViewer.Core.Dif;
using FileViewer.Core.Tests.TestSupport;

namespace FileViewer.Core.Tests.Dif;

public class DifSectionScannerTests
{
    private static DifFileLayout ScanFixture(string fileName) =>
        DifSectionScanner.Scan(FixtureLoader.ReadBytes(fileName));

    private static string ReadDataRegion(string fileName, DifSection section)
    {
        byte[] content = FixtureLoader.ReadBytes(fileName);
        return Encoding.UTF8.GetString(
            content, (int)section.DataStartOffset, (int)(section.DataEndOffsetExclusive - section.DataStartOffset));
    }

    [Fact]
    public void Scan_BulkFile_FindsEverySection()
    {
        DifFileLayout layout = ScanFixture("bulk_multi_section.dif");

        Assert.True(layout.IsValid);
        Assert.True(layout.IsMultiSection);
        Assert.Equal(2, layout.Sections.Count);
    }

    [Fact]
    public void Scan_BulkFile_NamesSectionsFromTheirDataAttribute()
    {
        DifFileLayout layout = ScanFixture("bulk_multi_section.dif");

        Assert.Equal(["DVD_HIST", "CALL_SCHEDULE"], layout.Sections.Select(s => s.Name));
        Assert.All(layout.Sections, section => Assert.True(section.HasDeclaredName));
    }

    [Fact]
    public void Scan_BulkFile_GivesEachSectionItsOwnColumns()
    {
        DifFileLayout layout = ScanFixture("bulk_multi_section.dif");

        // Each section's declared fields, with the implicit _ID/_ERR/_SIZE record prefix applied.
        Assert.Equal(["_ID", "_ERR", "_SIZE", "DECLARED_DATE", "EX_DATE", "DIVIDEND_AMOUNT"], layout.Sections[0].ColumnNames);
        Assert.Equal(["_ID", "_ERR", "_SIZE", "CALL_DATE", "CALL_PRICE"], layout.Sections[1].ColumnNames);
    }

    [Fact]
    public void Scan_BulkFile_BoundsEachSectionToItsOwnDataRows()
    {
        DifFileLayout layout = ScanFixture("bulk_multi_section.dif");

        string first = ReadDataRegion("bulk_multi_section.dif", layout.Sections[0]);
        string second = ReadDataRegion("bulk_multi_section.dif", layout.Sections[1]);

        Assert.Equal(2, first.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("SEC1 Equity", first);
        Assert.DoesNotContain("SEC3 Corp", first);

        Assert.Equal(3, second.Split('\n', StringSplitOptions.RemoveEmptyEntries).Length);
        Assert.Contains("SEC3 Corp", second);
        Assert.DoesNotContain("SEC1 Equity", second);
    }

    [Fact]
    public void Scan_BulkFile_AttributesEachSectionsOwnDataRecordsCount()
    {
        DifFileLayout layout = ScanFixture("bulk_multi_section.dif");

        Assert.Equal(2, layout.Sections[0].DeclaredDataRecords);
        Assert.Equal(3, layout.Sections[1].DeclaredDataRecords);
    }

    [Fact]
    public void Scan_BulkFile_KeepsSectionMetadataSeparateFromFileHeaderMetadata()
    {
        DifFileLayout layout = ScanFixture("bulk_multi_section.dif");

        Assert.Equal("dl123456", layout.HeaderMetadata["FIRMNAME"]);
        Assert.Equal("DVD_HIST", layout.Sections[0].Metadata[DifFormatOptions.SectionDataKey]);
        Assert.Contains("TIMESTARTED", layout.Sections[0].Metadata.Keys);
        Assert.DoesNotContain("FIRMNAME", layout.Sections[1].Metadata.Keys);
    }

    [Fact]
    public void Scan_BulkFile_ReadsFileLevelMarkersAndTrailer()
    {
        DifFileLayout layout = ScanFixture("bulk_multi_section.dif");

        Assert.Equal(DifFormatOptions.HeaderStartAlt, layout.HeaderMarker);
        Assert.Equal(DifFormatOptions.TrailerAlt, layout.TrailerMarker);
        Assert.True(layout.HasFileStartMarker);
        Assert.True(layout.HasFileEndMarker);
        Assert.Equal('|', layout.Delimiter);
    }

    [Fact]
    public void Scan_SectionsWithoutDataAttribute_GetSynthesizedNames()
    {
        DifFileLayout layout = ScanFixture("bulk_unnamed_sections.dif");

        Assert.Equal(2, layout.Sections.Count);
        Assert.Equal(["Section 1", "Section 2"], layout.Sections.Select(s => s.Name));
        Assert.All(layout.Sections, section => Assert.False(section.HasDeclaredName));
    }

    [Fact]
    public void Scan_OrdinarySingleSectionFile_ProducesOneSectionMatchingTheClassicParse()
    {
        byte[] content = FixtureLoader.ReadBytes("FixedIncomeAsia.dif");
        DifFileLayout layout = DifSectionScanner.Scan(content);
        DifFileHeader classic = DifHeaderParser.Parse(content);

        Assert.True(layout.IsValid);
        Assert.False(layout.IsMultiSection);
        Assert.Single(layout.Sections);
        Assert.Equal(classic.ColumnNames, layout.Sections[0].ColumnNames);
        Assert.Equal(classic.DataStartOffset, layout.Sections[0].DataStartOffset);
        Assert.Equal(classic.DataEndOffsetExclusive, layout.Sections[0].DataEndOffsetExclusive);
        Assert.Equal(classic.Delimiter, layout.Delimiter);
    }

    [Fact]
    public void Scan_GetDataStyleFile_MatchesTheClassicParse()
    {
        byte[] content = FixtureLoader.ReadBytes("bloomberg_getdata_implicit_prefix.dif");
        DifFileLayout layout = DifSectionScanner.Scan(content);
        DifFileHeader classic = DifHeaderParser.Parse(content);

        Assert.Single(layout.Sections);
        Assert.Equal(classic.ColumnNames, layout.Sections[0].ColumnNames);
        Assert.Equal(classic.DataStartOffset, layout.Sections[0].DataStartOffset);
        Assert.Equal(classic.DataEndOffsetExclusive, layout.Sections[0].DataEndOffsetExclusive);
        Assert.Equal(3, layout.Sections[0].DeclaredDataRecords);
    }

    [Fact]
    public void Scan_NotADifFile_IsInvalidRatherThanThrowing()
    {
        DifFileLayout layout = ScanFixture("not_a_dif_file.bin");

        Assert.False(layout.IsValid);
        Assert.Contains(layout.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Error);
    }

    [Fact]
    public void Scan_SectionMissingEndOfData_StillReturnsTheSectionWithAWarning()
    {
        DifFileLayout layout = ScanFixture("missing_end_of_data.dif");

        Assert.True(layout.IsValid);
        Assert.Single(layout.Sections);
        Assert.Contains(layout.Diagnostics, d => d.Severity == DifDiagnosticSeverity.Warning);
    }

    [Fact]
    public void GetSectionHeader_CarriesSectionIdentityAndBounds()
    {
        DifFileLayout layout = ScanFixture("bulk_multi_section.dif");

        DifFileHeader header = layout.GetSectionHeader(1);

        Assert.Equal("CALL_SCHEDULE", header.SectionName);
        Assert.Equal(1, header.SectionIndex);
        Assert.Equal(2, header.SectionCount);
        Assert.True(header.IsMultiSection);
        Assert.Equal(layout.Sections[1].DataStartOffset, header.DataStartOffset);
        Assert.Equal(layout.Sections[1].DataEndOffsetExclusive, header.DataEndOffsetExclusive);
    }

    [Theory]
    [InlineData("holdings_bulk_20260101.dif", true)]
    [InlineData("BULK_dividends.out", true)]
    [InlineData("fixedincome_asia.dif", false)]
    public void FileNameSuggestsBulk_MatchesCaseInsensitivelyOnTheNameOnly(string fileName, bool expected) =>
        Assert.Equal(expected, DifBulkDetection.FileNameSuggestsBulk(Path.Combine("C:", "exports", fileName)));

    [Fact]
    public void ContentSuggestsBulk_DetectsABulkFileThatIsNotNamedLikeOne()
    {
        Assert.True(DifBulkDetection.ContentSuggestsBulk(FixtureLoader.ReadBytes("bulk_multi_section.dif")));
        Assert.True(DifBulkDetection.ContentSuggestsBulk(FixtureLoader.ReadBytes("bulk_unnamed_sections.dif")));
        Assert.False(DifBulkDetection.ContentSuggestsBulk(FixtureLoader.ReadBytes("FixedIncomeAsia.dif")));
    }

    [Fact]
    public void TryExtractTrailingDataAttribute_ReadsANamePackedOntoTheMarkerLine()
    {
        Assert.True(DifSectionScanner.TryExtractTrailingDataAttribute(
            "START-OF-FIELDS|DATA=DVD_HIST|extra", DifFormatOptions.FieldsStart, out string name));
        Assert.Equal("DVD_HIST", name);

        Assert.False(DifSectionScanner.TryExtractTrailingDataAttribute(
            "START-OF-FIELDS", DifFormatOptions.FieldsStart, out _));
    }

    // ---- Concatenated file envelopes -------------------------------------------------------
    //
    // A real bulk delivery is not one envelope holding several field blocks; it is whole file
    // envelopes laid end to end, each with its own START-OF-FILE, preamble, DATA= name, fields,
    // data, per-envelope stats and END-OF-FILE. Both fixtures below are that shape.

    [Fact]
    public void Scan_ConcatenatedFileEnvelopes_FindsEverySectionAndKeepsItsName()
    {
        DifFileLayout layout = ScanFixture("bulk_concatenated_envelopes.dif");

        Assert.True(layout.IsValid);
        Assert.Equal(3, layout.Sections.Count);
        Assert.Equal(
            ["CALL_SCHEDULE", "DVD_HIST", "INDX_MEMBERS"],
            layout.Sections.Select(section => section.Name));
    }

    [Fact]
    public void Scan_ConcatenatedFileEnvelopes_GivesEachSectionItsOwnFieldList()
    {
        DifFileLayout layout = ScanFixture("bulk_concatenated_envelopes.dif");

        // Each envelope declares its own fields; the implicit _ID/_ERR/_SIZE prefix is added to each.
        Assert.Equal(["_ID", "_ERR", "_SIZE", "CALL_DATE", "CALL_PRICE"], layout.Sections[0].ColumnNames);
        Assert.Equal(["_ID", "_ERR", "_SIZE", "DECLARED_DATE", "EX_DATE", "DIVIDEND_AMOUNT"], layout.Sections[1].ColumnNames);
        Assert.Equal(["_ID", "_ERR", "_SIZE", "MEMBER_TICKER_AND_EXCHANGE_CODE", "PERCENT_WEIGHT"], layout.Sections[2].ColumnNames);
    }

    [Fact]
    public void Scan_ConcatenatedFileEnvelopes_AttributesEachEnvelopesOwnDataRecords()
    {
        // The per-envelope DATARECORDS sits after that envelope's END-OF-DATA and before its
        // END-OF-FILE, so it belongs to that section rather than to the file trailer.
        DifFileLayout layout = ScanFixture("bulk_concatenated_envelopes.dif");

        Assert.Equal(2, layout.Sections[0].DeclaredDataRecords);
        Assert.Equal(3, layout.Sections[1].DeclaredDataRecords);
        Assert.Equal(1, layout.Sections[2].DeclaredDataRecords);
    }

    [Fact]
    public void Scan_FileOpeningWithStartOfFileRatherThanAHeaderMarker_StillOpens()
    {
        // No INAHDR/IMAHDR anywhere: START-OF-FILE is the file's own first line. Rejecting these
        // meant a whole shape of real delivery could not be opened at all.
        DifFileLayout layout = ScanFixture("bulk_envelopes_no_header_marker.dif");

        Assert.True(layout.IsValid);
        Assert.Equal(3, layout.Sections.Count);
        Assert.True(layout.HasFileStartMarker);
        Assert.Equal(string.Empty, layout.HeaderMarker); // none to record, and none invented
    }

    [Fact]
    public void Scan_ConcatenatedFileEnvelopes_DoesNotSwallowLaterNamesIntoTheTrailer()
    {
        // The regression this guards: the first END-OF-FILE used to latch "the trailer has begun",
        // after which every later DATA= line was filed as trailer metadata instead of naming its
        // section, so everything after the first envelope came out as "Section 2", "Section 3", ...
        DifFileLayout layout = ScanFixture("bulk_concatenated_envelopes.dif");

        Assert.DoesNotContain(layout.Sections, section => section.Name.StartsWith("Section ", StringComparison.Ordinal));
    }
}
