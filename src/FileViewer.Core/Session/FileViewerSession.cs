using FileViewer.Core.Caching;
using FileViewer.Core.Dif;
using FileViewer.Core.Export;
using FileViewer.Core.Indexing;
using FileViewer.Core.Native;
using FileViewer.Core.Overlay;
using FileViewer.Core.Sorting;

namespace FileViewer.Core.Session;

/// <summary>
/// Facade tying together everything a consumer (the WPF app) needs to open, browse, edit, and
/// export a DIF file: the indexed <see cref="Indexing.FileIndex"/>, the <see cref="EditOverlay"/>,
/// a <see cref="DecodedRowCache"/>, and the current sort order over base rows. This is the main
/// object <c>FileViewer.App</c> talks to.
/// </summary>
public sealed class FileViewerSession : IDisposable
{
    private UnmanagedArray<SortKey>? _sortedOrder; // non-null only while a sort other than file order is active
    private bool _disposed;

    public FileIndex FileIndex { get; }

    /// <summary>The whole file's structure, including sections this session isn't showing — see <see cref="OpenSectionAsync"/>.</summary>
    public DifFileLayout Layout => FileIndex.Layout;

    /// <summary>Every data section the file declares. One entry for an ordinary export; one per <c>DATA=</c> block for a bulk export.</summary>
    public IReadOnlyList<DifSection> Sections => Layout.Sections;

    /// <summary>Which section this session is showing.</summary>
    public int SectionIndex => FileIndex.SectionIndex;
    public EditOverlay Overlay { get; } = new();
    public DecodedRowCache Cache { get; }

    /// <summary>Current sort order over base (file) rows. <see cref="FileIndex.SortKeys"/> (file order) unless <see cref="ApplySort"/> is active.</summary>
    public UnmanagedArray<SortKey> CurrentOrder => _sortedOrder ?? FileIndex.SortKeys;

    public SortDirection? CurrentSortDirection { get; private set; }

    private FileViewerSession(FileIndex fileIndex, int cacheCapacity)
    {
        FileIndex = fileIndex;
        Cache = new DecodedRowCache(cacheCapacity);
    }

    public static async Task<FileViewerSession> OpenAsync(
        string path,
        int cacheCapacity = DecodedRowCache.DefaultCapacity,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        FileIndex index = await FileIndexer.IndexAsync(path, progress, cancellationToken);
        return new FileViewerSession(index, cacheCapacity);
    }

    /// <summary>
    /// Opens one section of a file whose structure has already been scanned — how a bulk file's
    /// other sections are reached. Each section gets its own session (and therefore its own row
    /// index, edit overlay, cache, sort and filters), and only the sections actually opened are ever
    /// indexed.
    /// </summary>
    public static async Task<FileViewerSession> OpenSectionAsync(
        string path,
        DifFileLayout layout,
        int sectionIndex,
        int cacheCapacity = DecodedRowCache.DefaultCapacity,
        IProgress<IndexingProgress>? progress = null,
        CancellationToken cancellationToken = default)
    {
        FileIndex index = await FileIndexer.IndexSectionAsync(path, layout, sectionIndex, progress, cancellationToken);
        return new FileViewerSession(index, cacheCapacity);
    }

    /// <summary>Replaces <see cref="CurrentOrder"/> with a fresh sorted copy of the base rows. Never mutates <see cref="FileIndex.SortKeys"/> itself.</summary>
    public void ApplySort(SortDirection direction)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        UnmanagedArray<SortKey> newOrder = RowSorter.SortByKey(FileIndex.SortKeys, direction);
        _sortedOrder?.Dispose();
        _sortedOrder = newOrder;
        CurrentSortDirection = direction;
    }

    /// <summary>Resets <see cref="CurrentOrder"/> back to file order.</summary>
    public void ClearSort()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _sortedOrder?.Dispose();
        _sortedOrder = null;
        CurrentSortDirection = null;
    }

    public ResolvedRow? Resolve(long rowIndex) => RowResolver.Resolve(rowIndex, FileIndex, Overlay, Cache);

    /// <summary><see cref="CurrentOrder"/>'s base rows followed by any currently-live Added/Duplicated rows, in creation order.</summary>
    public IEnumerable<long> GetExportRowOrder()
    {
        UnmanagedArray<SortKey> order = CurrentOrder;
        for (nuint i = 0; i < order.Count; i++)
        {
            yield return order[i].RowIndex;
        }

        foreach (long rowIndex in Overlay.GetLiveAddedOrDuplicatedRowIndices())
        {
            yield return rowIndex;
        }
    }

    /// <summary>Exports every row of this section, in the session's current sort order.</summary>
    public void Export(Stream stream, IRowExporter exporter, int? maxRows = null) =>
        ExportRunner.Export(stream, FileIndex, Overlay, Cache, GetExportRowOrder(), exporter, maxRows);

    /// <summary>
    /// Exports exactly the rows in <paramref name="rowOrder"/>, in that order — what the caller
    /// needs to export "what is on screen" (the grid's filters and sort applied) or just the rows
    /// the user has ticked, rather than the whole section.
    /// </summary>
    public void Export(Stream stream, IRowExporter exporter, IEnumerable<long> rowOrder, int? maxRows = null) =>
        ExportRunner.Export(stream, FileIndex, Overlay, Cache, rowOrder, exporter, maxRows);

    public string GeneratePreview(IRowExporter exporter) =>
        PreviewGenerator.GeneratePreview(FileIndex, Overlay, Cache, GetExportRowOrder(), exporter);

    /// <summary>Preview of the same rows <see cref="Export(Stream, IRowExporter, IEnumerable{long}, int?)"/> would write, so the preview can't disagree with the export.</summary>
    public string GeneratePreview(IRowExporter exporter, IEnumerable<long> rowOrder) =>
        PreviewGenerator.GeneratePreview(FileIndex, Overlay, Cache, rowOrder, exporter);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sortedOrder?.Dispose();
        FileIndex.Dispose();
    }
}
