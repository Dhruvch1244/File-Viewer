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

    /// <summary>
    /// The file's full structural layout — every section it declares, not just the one this index
    /// covers. Carried here so switching to another section of a bulk file re-uses the scan that
    /// already happened instead of walking the file again (see <see cref="FileIndexer.IndexSectionAsync"/>).
    /// </summary>
    public DifFileLayout Layout { get; }

    /// <summary>Which of <see cref="Layout"/>'s sections this index covers. Always 0 for an ordinary single-section file.</summary>
    public int SectionIndex => Header.SectionIndex;
    public UnmanagedArray<RowIndexEntry> RowIndex { get; }
    public UnmanagedArray<SortKey> SortKeys { get; }
    public IReadOnlyList<DifDiagnostic> Diagnostics { get; }

    internal FileIndex(
        string filePath,
        long fileLength,
        SafeFileHandle fileHandle,
        DifFileHeader header,
        DifFileLayout layout,
        UnmanagedArray<RowIndexEntry> rowIndex,
        UnmanagedArray<SortKey> sortKeys,
        IReadOnlyList<DifDiagnostic> diagnostics)
    {
        FilePath = filePath;
        FileLength = fileLength;
        _fileHandle = fileHandle;
        Header = header;
        Layout = layout;
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

    /// <summary>
    /// Reads a row's bytes into a caller-supplied buffer, returning how many bytes it occupies (or
    /// -1 if <paramref name="destination"/> is too small, so the caller can grow and retry). The
    /// scanning paths (filtering, distinct values) go through this rather than
    /// <see cref="GetRowBytes"/> so that walking millions of rows reuses one pooled buffer per
    /// worker instead of allocating a fresh array per row.
    /// </summary>
    public int TryReadRowBytes(long rowIndex, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ref RowIndexEntry entry = ref RowIndex[rowIndex];
        if (entry.Length > destination.Length) return -1;
        return RandomAccessReader.ReadExactly(_fileHandle, destination[..entry.Length], entry.Offset);
    }

    /// <summary>
    /// Reads up to <paramref name="destination"/>.Length bytes of the file starting at
    /// <paramref name="offset"/>, returning how many were read. Used by the scanning paths to pull in
    /// a whole run of rows at once rather than issuing one read per row — rows are contiguous in file
    /// order, so a scan that walks them in that order is really a sequential read pretending to be
    /// millions of random ones.
    /// </summary>
    public int ReadBytes(Span<byte> destination, long offset)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || offset >= FileLength) return 0;

        int toRead = (int)Math.Min(destination.Length, FileLength - offset);
        return RandomAccessReader.ReadExactly(_fileHandle, destination[..toRead], offset);
    }

    /// <summary>The offset/length of a row as recorded by the indexer.</summary>
    public (long Offset, int Length) GetRowExtent(long rowIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ref RowIndexEntry entry = ref RowIndex[rowIndex];
        return (entry.Offset, entry.Length);
    }

    /// <summary>Byte length of a row as recorded by the indexer — lets a caller size a buffer before calling <see cref="TryReadRowBytes"/>.</summary>
    public int GetRowByteLength(long rowIndex)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return RowIndex[rowIndex].Length;
    }

    /// <summary>
    /// Copies <paramref name="length"/> bytes starting at <paramref name="offset"/> straight from the
    /// source file to <paramref name="destination"/>, unchanged. Used by <see cref="Export.DifExporter"/>
    /// to reproduce header/trailer bytes byte-for-byte instead of reconstructing them from parsed
    /// metadata, so anything the parser doesn't fully model (exact spacing, an unrecognized line, a
    /// stale DATARECORDS count, ...) still survives a round trip untouched.
    /// </summary>
    public void CopyRangeTo(Stream destination, long offset, long length)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (length <= 0) return;

        byte[] buffer = new byte[(int)Math.Min(length, 1 << 20)];
        long remaining = length;
        long position = offset;
        while (remaining > 0)
        {
            int chunkSize = (int)Math.Min(remaining, buffer.Length);
            int read = RandomAccessReader.ReadExactly(_fileHandle, buffer.AsSpan(0, chunkSize), position);
            if (read == 0) break; // source file ended early (shouldn't happen; nothing more to copy)
            destination.Write(buffer, 0, read);
            position += read;
            remaining -= read;
        }
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
