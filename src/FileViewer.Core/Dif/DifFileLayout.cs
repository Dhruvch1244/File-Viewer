namespace FileViewer.Core.Dif;

/// <summary>
/// Everything structural about a DIF file that isn't a data row: the file-level header preamble,
/// every <see cref="DifSection"/> it contains, and the trailer. An ordinary export produces a
/// one-section layout; a "bulk" export produces one section per <c>DATA=</c> block.
///
/// This exists so a file is opened (and its sections listed) once, while the expensive part —
/// indexing a section's rows — happens per section, on demand: switching to another section of a
/// bulk file never re-scans the file's structure, and sections the user never opens are never
/// indexed at all.
/// </summary>
public sealed class DifFileLayout
{
    public string HeaderMarker { get; init; } = DifFormatOptions.HeaderStart;
    public bool HasFileStartMarker { get; init; }
    public required IReadOnlyDictionary<string, string> HeaderMetadata { get; init; }
    public required char Delimiter { get; init; }
    public required IReadOnlyList<DifSection> Sections { get; init; }
    public required IReadOnlyDictionary<string, string> TrailerMetadata { get; init; }
    public string TrailerMarker { get; init; } = DifFormatOptions.Trailer;
    public bool HasFileEndMarker { get; init; }

    /// <summary>Byte offset where the trailer begins — the first byte after the last section's END-OF-DATA line. Used by <see cref="Export.DifExporter"/> to reproduce the trailer verbatim when exporting one section of a multi-section file.</summary>
    public required long TrailerStartOffset { get; init; }

    /// <summary>False if a required marker could not be located — the file can't be treated as DIF at all.</summary>
    public required bool IsValid { get; init; }

    public required IReadOnlyList<DifDiagnostic> Diagnostics { get; init; }

    /// <summary>True when the file carries more than one data section — i.e. it really is a bulk export, whatever its name.</summary>
    public bool IsMultiSection => Sections.Count > 1;

    /// <summary>Builds the per-section <see cref="DifFileHeader"/> the indexer, grid, and exporters work against. For a one-section file this is exactly the header the classic head/tail parse produces.</summary>
    public DifFileHeader GetSectionHeader(int sectionIndex)
    {
        if (!IsValid || Sections.Count == 0)
        {
            return DifHeaderParser.CreateInvalidHeader(Diagnostics);
        }

        DifSection section = Sections[Math.Clamp(sectionIndex, 0, Sections.Count - 1)];
        return new DifFileHeader
        {
            HeaderMarker = HeaderMarker,
            HasFileStartMarker = HasFileStartMarker,
            HeaderMetadata = HeaderMetadata,
            Delimiter = Delimiter,
            ColumnNames = section.ColumnNames,
            PostFieldsMetadata = section.Metadata,
            TrailerMetadata = TrailerMetadata,
            TrailerMarker = TrailerMarker,
            HasFileEndMarker = HasFileEndMarker,
            DeclaredDataRecords = section.DeclaredDataRecords,
            DataStartOffset = section.DataStartOffset,
            DataEndOffsetExclusive = section.DataEndOffsetExclusive,
            IsValid = true,
            Diagnostics = Diagnostics,
            SectionName = section.Name,
            SectionIndex = section.Index,
            SectionCount = Sections.Count,
            SectionBlockStartOffset = section.BlockStartOffset,
            TrailerStartOffset = TrailerStartOffset,
        };
    }

    public static DifFileLayout Invalid(IReadOnlyList<DifDiagnostic> diagnostics) => new()
    {
        HeaderMetadata = new Dictionary<string, string>(),
        Delimiter = DifFormatOptions.DefaultDelimiter,
        Sections = [],
        TrailerMetadata = new Dictionary<string, string>(),
        TrailerStartOffset = 0,
        IsValid = false,
        Diagnostics = diagnostics,
    };

    /// <summary>
    /// Wraps the result of the classic (single-section) head/tail parse as a one-section layout, so
    /// every consumer can work in terms of sections without the cheap path having to go through the
    /// sequential section scan.
    /// </summary>
    public static DifFileLayout FromSingleSectionHeader(DifFileHeader header, long fileLength)
    {
        if (!header.IsValid)
        {
            return Invalid(header.Diagnostics);
        }

        var section = new DifSection
        {
            Index = 0,
            Name = DifSectionScanner.SynthesizeSectionName(0),
            HasDeclaredName = false,
            ColumnNames = header.ColumnNames,
            Metadata = header.PostFieldsMetadata,
            BlockStartOffset = 0,
            DataStartOffset = header.DataStartOffset,
            DataEndOffsetExclusive = header.DataEndOffsetExclusive,
            DataEndLineEndOffset = Math.Min(fileLength, header.DataEndOffsetExclusive),
            DeclaredDataRecords = header.DeclaredDataRecords,
        };

        return new DifFileLayout
        {
            HeaderMarker = header.HeaderMarker,
            HasFileStartMarker = header.HasFileStartMarker,
            HeaderMetadata = header.HeaderMetadata,
            Delimiter = header.Delimiter,
            Sections = [section],
            TrailerMetadata = header.TrailerMetadata,
            TrailerMarker = header.TrailerMarker,
            HasFileEndMarker = header.HasFileEndMarker,
            TrailerStartOffset = header.DataEndOffsetExclusive,
            IsValid = true,
            Diagnostics = header.Diagnostics,
        };
    }
}
