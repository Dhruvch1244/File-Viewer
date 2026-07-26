namespace FileViewer.Core.Dif;

/// <summary>
/// Result of parsing a DIF file's header, field-name list, and trailer (everything except the
/// bulk data rows). Carries the byte offsets bounding the data section so the indexer's parallel
/// chunk scan (Phase B) knows exactly which range to scan without touching header/trailer bytes.
/// </summary>
public sealed class DifFileHeader
{
    public required IReadOnlyDictionary<string, string> HeaderMetadata { get; init; }
    public required char Delimiter { get; init; }
    public required IReadOnlyList<string> ColumnNames { get; init; }
    public required IReadOnlyDictionary<string, string> TrailerMetadata { get; init; }
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
