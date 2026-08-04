using FileViewer.Core.Caching;
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

    public void Export(Stream stream, IRowExporter exporter, int? maxRows = null) =>
        ExportRunner.Export(stream, FileIndex, Overlay, Cache, GetExportRowOrder(), exporter, maxRows);

    public string GeneratePreview(IRowExporter exporter) =>
        PreviewGenerator.GeneratePreview(FileIndex, Overlay, Cache, GetExportRowOrder(), exporter);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        _sortedOrder?.Dispose();
        FileIndex.Dispose();
    }
}
