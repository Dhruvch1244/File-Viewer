using FileViewer.Core.Caching;
using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Export;

/// <summary>
/// Generates a 2-row export preview by reusing <see cref="ExportRunner"/>'s exact driver, capped at
/// <see cref="PreviewRowCount"/> rows — guarantees the preview can never drift from what a real
/// export of the same state would produce.
/// </summary>
public static class PreviewGenerator
{
    public const int PreviewRowCount = 2;

    public static string GeneratePreview(
        FileIndex fileIndex,
        EditOverlay overlay,
        DecodedRowCache cache,
        IEnumerable<long> orderedRowIndices,
        IRowExporter exporter)
    {
        using var memoryStream = new MemoryStream();
        ExportRunner.Export(memoryStream, fileIndex, overlay, cache, orderedRowIndices, exporter, maxRows: PreviewRowCount);
        return DifFormatOptions.TextEncoding.GetString(memoryStream.ToArray());
    }
}
