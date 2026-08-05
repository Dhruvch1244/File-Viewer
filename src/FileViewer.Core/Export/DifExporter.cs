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
        sourceFileIndex.CopyRangeTo(stream, 0, sourceFileIndex.Header.DataStartOffset);
        _writer = new StreamWriter(stream, DifFormatOptions.TextEncoding, leaveOpen: true) { NewLine = "\n" };
    }

    public void WriteRow(ResolvedRow row)
    {
        _writer!.WriteLine(string.Join(sourceFileIndex.Header.Delimiter, row.FieldValues));
    }

    public void End()
    {
        _writer!.Flush();

        // DataEndOffsetExclusive points at the start of the END-OF-DATA line itself, so this one
        // copy carries END-OF-DATA, all trailer metadata (DATARECORDS included, even if it no
        // longer matches the row count after edits — untouched on purpose, per product decision),
        // any END-OF-FILE line, and the closing INATRL/IMATRL marker through to end of file.
        long trailerStart = sourceFileIndex.Header.DataEndOffsetExclusive;
        sourceFileIndex.CopyRangeTo(_stream!, trailerStart, sourceFileIndex.FileLength - trailerStart);
    }

    public void Dispose() => _writer?.Dispose();
}
