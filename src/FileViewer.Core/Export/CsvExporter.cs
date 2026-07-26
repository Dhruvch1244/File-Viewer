using FileViewer.Core.Dif;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Export;

/// <summary>RFC 4180-style CSV: fields containing the delimiter, a quote, or a line break are quoted, with embedded quotes doubled.</summary>
public sealed class CsvExporter : IRowExporter
{
    private const char Delimiter = ',';
    private StreamWriter? _writer;

    public void Begin(Stream stream, IReadOnlyList<string> columnNames)
    {
        _writer = new StreamWriter(stream, DifFormatOptions.TextEncoding, leaveOpen: true) { NewLine = "\r\n" };
        WriteDelimitedLine(columnNames);
    }

    public void WriteRow(ResolvedRow row) => WriteDelimitedLine(row.FieldValues);

    public void End() => _writer!.Flush();

    public void Dispose() => _writer?.Dispose();

    private void WriteDelimitedLine(IReadOnlyList<string> fields)
    {
        for (int i = 0; i < fields.Count; i++)
        {
            if (i > 0) _writer!.Write(Delimiter);
            _writer!.Write(QuoteIfNeeded(fields[i]));
        }
        _writer!.WriteLine();
    }

    private static string QuoteIfNeeded(string field)
    {
        bool needsQuoting = field.IndexOfAny([Delimiter, '"', '\r', '\n']) >= 0;
        return needsQuoting ? "\"" + field.Replace("\"", "\"\"") + "\"" : field;
    }
}
