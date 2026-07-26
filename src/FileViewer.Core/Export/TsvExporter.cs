using FileViewer.Core.Dif;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Export;

/// <summary>
/// Tab-separated values. Unlike CSV, TSV has no widely-adopted quoting convention, so an embedded
/// tab/CR/LF in a field is replaced with a single space rather than escaped — a deliberate,
/// documented simplification that keeps the output structurally valid at the cost of losing the
/// original whitespace inside that one field.
/// </summary>
public sealed class TsvExporter : IRowExporter
{
    private const char Delimiter = '\t';
    private StreamWriter? _writer;

    public void Begin(Stream stream, IReadOnlyList<string> columnNames)
    {
        _writer = new StreamWriter(stream, DifFormatOptions.TextEncoding, leaveOpen: true) { NewLine = "\n" };
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
            _writer!.Write(Sanitize(fields[i]));
        }
        _writer!.WriteLine();
    }

    private static string Sanitize(string field) =>
        field.IndexOfAny(['\t', '\r', '\n']) < 0 ? field : field.Replace('\t', ' ').Replace('\r', ' ').Replace('\n', ' ');
}
