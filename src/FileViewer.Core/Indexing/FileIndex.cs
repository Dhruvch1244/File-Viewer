using Microsoft.Win32.SafeHandles;
using FileViewer.Core.Dif;
using FileViewer.Core.Native;

namespace FileViewer.Core.Indexing;

/// <summary>
/// Owns a read-only handle onto a DIF file plus the unmanaged row index and sort-key arrays built
/// by <see cref="FileIndexer"/>. The source file is never mutated — <see cref="GetRowBytes"/> reads
/// a row's bytes on demand via <see cref="RandomAccess"/> rather than through a persistent
/// memory-mapped view of the whole file: mapping the entire file into one contiguous view up front
/// is an all-or-nothing commitment that can fail on Windows (ERROR_NOT_ENOUGH_MEMORY) depending on
/// how much memory/page-file space happens to be free on the machine at that moment — a real failure
/// this project hit in practice, independent of the file's actual size (see git history). Reading
/// each row on demand instead means opening the file never requires reserving space for the whole
/// thing at once.
/// </summary>
public sealed class FileIndex : IDisposable
{
    private readonly SafeFileHandle _fileHandle;
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
        SafeFileHandle fileHandle,
        DifFileHeader header,
        UnmanagedArray<RowIndexEntry> rowIndex,
        UnmanagedArray<SortKey> sortKeys,
        IReadOnlyList<DifDiagnostic> diagnostics)
    {
        FilePath = filePath;
        FileLength = fileLength;
        _fileHandle = fileHandle;
        Header = header;
        RowIndex = rowIndex;
        SortKeys = sortKeys;
        Diagnostics = diagnostics;
    }

    /// <summary>
    /// Raw bytes of the row at the given base row index, read fresh from disk into a small
    /// newly-allocated buffer — not a zero-copy slice of a persistent mapping. Safe to call
    /// concurrently from multiple threads (<see cref="RandomAccess.Read(SafeFileHandle,Span{byte},long)"/>
    /// takes an explicit file offset per call, with no shared file-position state to race on).
    /// </summary>
    public ReadOnlySpan<byte> GetRowBytes(long rowIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ref RowIndexEntry entry = ref RowIndex[rowIndex];
        byte[] buffer = new byte[entry.Length];
        RandomAccessReader.ReadExactly(_fileHandle, buffer, entry.Offset);
        return buffer;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        RowIndex.Dispose();
        SortKeys.Dispose();
        _fileHandle.Dispose();
    }
}
