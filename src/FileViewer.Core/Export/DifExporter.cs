using FileViewer.Core.Dif;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Export;

/// <summary>Writes the same DIF grammar <see cref="DifHeaderParser"/> reads, so round-tripping an export back through the parser reproduces the resolved rows.</summary>
public sealed class DifExporter(char delimiter, IReadOnlyDictionary<string, string>? headerMetadata = null) : IRowExporter
{
    private StreamWriter? _writer;
    private int _rowCount;

    public void Begin(Stream stream, IReadOnlyList<string> columnNames)
    {
        _rowCount = 0;
        _writer = new StreamWriter(stream, DifFormatOptions.TextEncoding, leaveOpen: true) { NewLine = "\n" };

        _writer.WriteLine(DifFormatOptions.HeaderStart);
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
        _writer.WriteLine(DifFormatOptions.Trailer);
        _writer.WriteLine($"{DifFormatOptions.DataRecordsKey}={_rowCount}");
        _writer.Flush();
    }

    public void Dispose() => _writer?.Dispose();
}
