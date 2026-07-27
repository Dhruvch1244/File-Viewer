using FileViewer.Core.Dif;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Export;

/// <summary>
/// Writes the same DIF grammar <see cref="DifHeaderParser"/> reads, so round-tripping an export
/// back through the parser reproduces the resolved rows. When constructed <see cref="ForHeader"/> a
/// source <see cref="DifFileHeader"/>, also reproduces everything that header parsed but isn't
/// itself a column value — the header marker spelling (INAHDR/IMAHDR), the START-OF-FILE marker,
/// pre-fields metadata (FIRMNAME, PROGRAMNAME, ...), post-fields metadata (TIMESTARTED, ...),
/// trailer metadata beyond DATARECORDS, the closing trailer marker spelling (INATRL/IMATRL), and the
/// END-OF-FILE marker — so an export of an edited file keeps looking like the source file it came
/// from instead of shedding everything the row grid doesn't display.
/// </summary>
public sealed class DifExporter(
    char delimiter,
    IReadOnlyDictionary<string, string>? headerMetadata = null,
    IReadOnlyDictionary<string, string>? postFieldsMetadata = null,
    IReadOnlyDictionary<string, string>? trailerMetadata = null,
    string headerMarker = DifFormatOptions.HeaderStart,
    bool includeFileStartMarker = false,
    string trailerMarker = DifFormatOptions.Trailer,
    bool includeFileEndMarker = false) : IRowExporter
{
    private StreamWriter? _writer;
    private int _rowCount;

    /// <summary>Convenience factory that carries every preservable piece of a parsed header straight into the exporter, so a "re-export what I opened" path never has to enumerate them by hand.</summary>
    public static DifExporter ForHeader(DifFileHeader header) => new(
        header.Delimiter,
        header.HeaderMetadata,
        header.PostFieldsMetadata,
        header.TrailerMetadata,
        header.HeaderMarker,
        header.HasFileStartMarker,
        header.TrailerMarker,
        header.HasFileEndMarker);

    public void Begin(Stream stream, IReadOnlyList<string> columnNames)
    {
        _rowCount = 0;
        _writer = new StreamWriter(stream, DifFormatOptions.TextEncoding, leaveOpen: true) { NewLine = "\n" };

        _writer.WriteLine(headerMarker);
        if (includeFileStartMarker)
        {
            _writer.WriteLine(DifFormatOptions.FileStart);
        }
        _writer.WriteLine($"{DifFormatOptions.DelimiterKey}={delimiter}");
        if (headerMetadata is not null)
        {
            foreach ((string key, string value) in headerMetadata)
            {
                if (key == DifFormatOptions.DelimiterKey) continue; // already written above
                _writer.WriteLine($"{key}={value}");
            }
        }
        _writer.WriteLine(DifFormatOptions.FieldsStart);
        foreach (string name in columnNames)
        {
            _writer.WriteLine(name);
        }
        _writer.WriteLine(DifFormatOptions.FieldsEnd);
        if (postFieldsMetadata is not null)
        {
            foreach ((string key, string value) in postFieldsMetadata)
            {
                _writer.WriteLine($"{key}={value}");
            }
        }
        _writer.WriteLine(DifFormatOptions.DataStart);
    }

    public void WriteRow(ResolvedRow row)
    {
        _writer!.WriteLine(string.Join(delimiter, row.FieldValues));
        _rowCount++;
    }

    public void End()
    {
        _writer!.WriteLine(DifFormatOptions.DataEnd);

        // DATARECORDS must reflect what was actually written (edits can add/remove rows), so it's
        // always recomputed here rather than trusting the source file's original value — every
        // *other* trailer key the source file had is still reproduced. Metadata is written before
        // the closing marker(s), matching how real "getdata"-style exports structure their trailer
        // (DATARECORDS/TIMEFINISHED directly after END-OF-DATA, then an optional END-OF-FILE line,
        // then the INATRL/IMATRL marker as the very last line) — but DifHeaderParser's trailer scan
        // is order-independent, so this ordering is a choice, not a requirement for round-tripping.
        bool wroteDataRecords = false;
        if (trailerMetadata is not null)
        {
            foreach ((string key, string value) in trailerMetadata)
            {
                string outValue = key == DifFormatOptions.DataRecordsKey ? _rowCount.ToString() : value;
                _writer.WriteLine($"{key}={outValue}");
                wroteDataRecords |= key == DifFormatOptions.DataRecordsKey;
            }
        }
        if (!wroteDataRecords)
        {
            _writer.WriteLine($"{DifFormatOptions.DataRecordsKey}={_rowCount}");
        }
        if (includeFileEndMarker)
        {
            _writer.WriteLine(DifFormatOptions.FileEnd);
        }
        _writer.WriteLine(trailerMarker);
        _writer.Flush();
    }

    public void Dispose() => _writer?.Dispose();
}
