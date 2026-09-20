using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Export;

/// <summary>
/// Writes a DIF export by copying everything outside the data section straight from the source
/// file, byte-for-byte, and only regenerating the rows between START-OF-DATA and END-OF-DATA. This
/// deliberately does NOT reconstruct the header/trailer from parsed metadata: whatever the parser
/// doesn't fully model (exact spacing, an unrecognized line, a since-stale DATARECORDS count after
/// edits, the header marker spelling, START-OF-FILE/END-OF-FILE, ...) still round-trips intact
/// because it was never re-derived in the first place — it's the same bytes the source file had.
/// </summary>
public sealed class DifExporter(FileIndex sourceFileIndex) : IRowExporter
{
    private Stream? _stream;
    private StreamWriter? _writer;

    public void Begin(Stream stream, IReadOnlyList<string> columnNames)
    {
        _stream = stream;
        DifFileHeader header = sourceFileIndex.Header;

        if (header.IsMultiSection)
        {
            // Exporting one section of a bulk file produces an ordinary single-section DIF: the
            // file-level preamble, then this section's own block (its DATA= attribute and field
            // list), skipping every other section's block and data entirely.
            long preambleEnd = sourceFileIndex.Layout.Sections[0].BlockStartOffset;
            sourceFileIndex.CopyRangeTo(stream, 0, preambleEnd);
            sourceFileIndex.CopyRangeTo(stream, header.SectionBlockStartOffset, header.DataStartOffset - header.SectionBlockStartOffset);
        }
        else
        {
            sourceFileIndex.CopyRangeTo(stream, 0, header.DataStartOffset);
        }

        _writer = new StreamWriter(stream, DifFormatOptions.TextEncoding, leaveOpen: true) { NewLine = "\n" };
    }

    public void WriteRow(ResolvedRow row)
    {
        // Field by field rather than string.Join: joining allocates a whole row's worth of string
        // per row, and an export of a few million rows is exactly where that shows up.
        char delimiter = sourceFileIndex.Header.Delimiter;
        IReadOnlyList<string> fields = row.FieldValues;
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) _writer!.Write(delimiter);
            _writer!.Write(fields[i]);
        }
        _writer!.WriteLine();
    }

    public void End()
    {
        DifFileHeader header = sourceFileIndex.Header;

        if (header.IsMultiSection)
        {
            // The source bytes between this section's END-OF-DATA and the file trailer belong to
            // other sections, so the closing marker is the one thing here that is written rather
            // than copied; the trailer itself still comes straight from the file.
            _writer!.WriteLine(DifFormatOptions.DataEnd);
            _writer.Flush();
            long bulkTrailerStart = sourceFileIndex.Layout.TrailerStartOffset;
            sourceFileIndex.CopyRangeTo(_stream!, bulkTrailerStart, sourceFileIndex.FileLength - bulkTrailerStart);
            return;
        }

        _writer!.Flush();

        // DataEndOffsetExclusive points at the start of the END-OF-DATA line itself, so this one
        // copy carries END-OF-DATA, all trailer metadata (DATARECORDS included, even if it no
        // longer matches the row count after edits — untouched on purpose, per product decision),
        // any END-OF-FILE line, and the closing INATRL/IMATRL marker through to end of file.
        long trailerStart = header.DataEndOffsetExclusive;
        sourceFileIndex.CopyRangeTo(_stream!, trailerStart, sourceFileIndex.FileLength - trailerStart);
    }

    public void Dispose() => _writer?.Dispose();
}
