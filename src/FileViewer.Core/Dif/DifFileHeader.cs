namespace FileViewer.Core.Dif;

/// <summary>
/// Result of parsing a DIF file's header, field-name list, and trailer (everything except the
/// bulk data rows). Carries the byte offsets bounding the data section so the indexer's parallel
/// chunk scan (Phase B) knows exactly which range to scan without touching header/trailer bytes.
/// </summary>
public sealed class DifFileHeader
{
    /// <summary>
    /// The literal marker line the source file opened with — <see cref="DifFormatOptions.HeaderStart"/>
    /// ("INAHDR") or <see cref="DifFormatOptions.HeaderStartAlt"/> ("IMAHDR"). Preserved so a DIF
    /// export reproduces the same marker the file was opened with rather than always normalizing to
    /// one spelling. Defaults to <see cref="DifFormatOptions.HeaderStart"/> for a header built other
    /// than by parsing a real file (e.g. <see cref="DifHeaderParser.CreateInvalidHeader"/>).
    /// </summary>
    public string HeaderMarker { get; init; } = DifFormatOptions.HeaderStart;

    /// <summary>Whether the source file had a <see cref="DifFormatOptions.FileStart"/> marker line right after the header marker.</summary>
    public bool HasFileStartMarker { get; init; }

    /// <summary>KEY=VALUE metadata lines between the header marker and START-OF-FIELDS (e.g. FIRMNAME, PROGRAMNAME, DELIMITER).</summary>
    public required IReadOnlyDictionary<string, string> HeaderMetadata { get; init; }

    public required char Delimiter { get; init; }
    public required IReadOnlyList<string> ColumnNames { get; init; }

    /// <summary>
    /// KEY=VALUE metadata lines some exports place between END-OF-FIELDS and START-OF-DATA (e.g.
    /// TIMESTARTED) — kept separate from <see cref="HeaderMetadata"/> so a DIF export can reproduce
    /// them in their original position rather than folding them into the header preamble.
    /// </summary>
    public IReadOnlyDictionary<string, string> PostFieldsMetadata { get; init; } = new Dictionary<string, string>();

    public required IReadOnlyDictionary<string, string> TrailerMetadata { get; init; }

    /// <summary>
    /// The literal closing marker line the source file used — <see cref="DifFormatOptions.Trailer"/>
    /// ("INATRL") or <see cref="DifFormatOptions.TrailerAlt"/> ("IMATRL") — mirroring
    /// <see cref="HeaderMarker"/> at the other end of the file. Defaults to
    /// <see cref="DifFormatOptions.Trailer"/> if the file had no trailer marker at all (trailer
    /// metadata alone, with nothing to close it, is tolerated — see <see cref="DifHeaderParser.ParseTrailerAndDataEnd"/>).
    /// </summary>
    public string TrailerMarker { get; init; } = DifFormatOptions.Trailer;

    /// <summary>Whether the source file had an <see cref="DifFormatOptions.FileEnd"/> marker line, mirroring <see cref="HasFileStartMarker"/> at the other end of the file.</summary>
    public bool HasFileEndMarker { get; init; }

    public int? DeclaredDataRecords { get; init; }

    /// <summary>Byte offset of the first data row (immediately after the START-OF-DATA line).</summary>
    public required long DataStartOffset { get; init; }

    /// <summary>
    /// Exclusive byte offset where the data section ends: the start of the END-OF-DATA line, or
    /// end-of-file if no END-OF-DATA marker was found within the trailing scan window.
    /// </summary>
    public required long DataEndOffsetExclusive { get; init; }

    /// <summary>
    /// False if a required marker (INAHDR/START-OF-FIELDS/END-OF-FIELDS/START-OF-DATA) could not
    /// be located — the file cannot be indexed as DIF at all (e.g. arbitrary binary input).
    /// </summary>
    public required bool IsValid { get; init; }

    public required IReadOnlyList<DifDiagnostic> Diagnostics { get; init; }

    public int ColumnIndexOf(string columnName)
    {
        for (int i = 0; i < ColumnNames.Count; i++)
        {
            if (string.Equals(ColumnNames[i], columnName, StringComparison.Ordinal)) return i;
        }
        return -1;
    }
}
