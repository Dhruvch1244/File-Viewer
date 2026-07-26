using System.IO.MemoryMappedFiles;
using FileViewer.Core.Dif;
using FileViewer.Core.Native;

namespace FileViewer.Core.Indexing;

/// <summary>
/// Owns the read-only memory-mapped view over a DIF file plus the unmanaged row index and sort-key
/// arrays built by <see cref="FileIndexer"/>. The source file is never mutated — reads happen
/// directly against the mapped bytes via <see cref="GetRowBytes"/>, with no per-row copy.
/// </summary>
public sealed unsafe class FileIndex : IDisposable
{
    private readonly MemoryMappedFile _mappedFile;
    private readonly MemoryMappedViewAccessor _accessor;
    private byte* _pointer;
    private bool _disposed;

    public string FilePath { get; }
    public long FileLength { get; }
    public DifFileHeader Header { get; }
    public UnmanagedArray<RowIndexEntry> RowIndex { get; }
    public UnmanagedArray<SortKey> SortKeys { get; }
    public IReadOnlyList<DifDiagnostic> Diagnostics { get; }

    internal FileIndex(
        string filePath,
        long fileLength,
        MemoryMappedFile mappedFile,
        MemoryMappedViewAccessor accessor,
        byte* pointer,
        DifFileHeader header,
        UnmanagedArray<RowIndexEntry> rowIndex,
        UnmanagedArray<SortKey> sortKeys,
        IReadOnlyList<DifDiagnostic> diagnostics)
    {
        FilePath = filePath;
        FileLength = fileLength;
        _mappedFile = mappedFile;
        _accessor = accessor;
        _pointer = pointer;
        Header = header;
        RowIndex = rowIndex;
        SortKeys = sortKeys;
        Diagnostics = diagnostics;
    }

    /// <summary>Raw bytes of the row at the given base row index, sliced directly from the mapped file — no copy.</summary>
    public ReadOnlySpan<byte> GetRowBytes(long rowIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ref RowIndexEntry entry = ref RowIndex[rowIndex];
        return new ReadOnlySpan<byte>(_pointer + entry.Offset, entry.Length);
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RowIndex.Dispose();
        SortKeys.Dispose();

        if (_pointer != null)
        {
            _accessor.SafeMemoryMappedViewHandle.ReleasePointer();
            _pointer = null;
        }
        _accessor.Dispose();
        _mappedFile.Dispose();
    }
}
