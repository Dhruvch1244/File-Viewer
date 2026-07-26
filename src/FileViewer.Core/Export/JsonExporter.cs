using System.Text.Json;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Export;

/// <summary>Streams a JSON array of row objects (column name -&gt; field value) via <see cref="Utf8JsonWriter"/> for correct escaping.</summary>
public sealed class JsonExporter : IRowExporter
{
    private Utf8JsonWriter? _writer;
    private IReadOnlyList<string>? _columnNames;

    public void Begin(Stream stream, IReadOnlyList<string> columnNames)
    {
        _columnNames = columnNames;
        _writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = false });
        _writer.WriteStartArray();
    }

    public void WriteRow(ResolvedRow row)
    {
        _writer!.WriteStartObject();
        int fieldCount = Math.Min(_columnNames!.Count, row.FieldValues.Count);
        for (int i = 0; i < fieldCount; i++)
        {
            _writer.WriteString(_columnNames[i], row.FieldValues[i]);
        }
        _writer.WriteEndObject();
    }

    public void End()
    {
        _writer!.WriteEndArray();
        _writer.Flush();
    }

    public void Dispose() => _writer?.Dispose();
}
