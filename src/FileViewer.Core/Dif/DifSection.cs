namespace FileViewer.Core.Dif;

/// <summary>
/// One data block of a DIF file: a START-OF-FIELDS/END-OF-FIELDS field list followed by a
/// START-OF-DATA/END-OF-DATA row region. An ordinary export has exactly one; a "bulk" export
/// repeats the whole block once per requested bulk field, each block naming itself with a
/// <c>DATA=&lt;something&gt;</c> attribute (<see cref="DifFormatOptions.SectionDataKey"/>) — see
/// <see cref="DifSectionScanner"/>.
///
/// Offsets are absolute file offsets, so a section can be indexed (and exported) on its own
/// without re-reading anything that precedes it.
/// </summary>
public sealed class DifSection
{
    /// <summary>Zero-based position of this section in the file.</summary>
    public required int Index { get; init; }

    /// <summary>
    /// The section's <c>DATA=</c> attribute value, or a synthesized <c>"Section N"</c> when the
    /// file declares none (a single-section file, or a bulk file that omits the attribute) — never
    /// empty, since this is what the UI labels the section with.
    /// </summary>
    public required string Name { get; init; }

    /// <summary>True when <see cref="Name"/> came from a real <c>DATA=</c> attribute rather than being synthesized.</summary>
    public required bool HasDeclaredName { get; init; }

    /// <summary>This section's own column list, with the implicit record prefix applied (see <see cref="DifFormatOptions.ImplicitRecordPrefixColumns"/>). Each section of a bulk file declares its own.</summary>
    public required IReadOnlyList<string> ColumnNames { get; init; }

    /// <summary>KEY=VALUE metadata attached to this section — everything between the preceding section's END-OF-DATA (or the file header) and this section's START-OF-DATA, including its own <c>DATA=</c> line.</summary>
    public required IReadOnlyDictionary<string, string> Metadata { get; init; }

    /// <summary>Byte offset of the first line belonging to this section's block (its first metadata line, or its START-OF-FIELDS) — the start of the run of bytes a single-section DIF export reproduces verbatim.</summary>
    public required long BlockStartOffset { get; init; }

    /// <summary>Byte offset of this section's first data row (immediately after its START-OF-DATA line).</summary>
    public required long DataStartOffset { get; init; }

    /// <summary>Exclusive byte offset where this section's data ends: the start of its END-OF-DATA line (or end of file if it has none).</summary>
    public required long DataEndOffsetExclusive { get; init; }

    /// <summary>Byte offset of the first byte after this section's END-OF-DATA line — where the next section's block (or the file trailer) begins.</summary>
    public required long DataEndLineEndOffset { get; init; }

    /// <summary>This section's own DATARECORDS count, if it declared one (either in its metadata or in the trailer lines directly following its END-OF-DATA).</summary>
    public int? DeclaredDataRecords { get; set; }

    public long DataLength => Math.Max(0, DataEndOffsetExclusive - DataStartOffset);
}
