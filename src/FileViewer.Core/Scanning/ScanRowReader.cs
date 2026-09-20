using System.Buffers;
using FileViewer.Core.Caching;
using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Scanning;

/// <summary>
/// Reads rows for one worker of a whole-file scan — filtering, distinct values, column statistics,
/// arbitrary-column sorting. Three things it does that a plain per-row read does not:
/// <list type="bullet">
/// <item>keeps one rented row buffer for every row it touches, rather than allocating per row;</item>
/// <item>when the rows come in file order, reads them in <see cref="ScanChunkBytes"/> runs and serves
/// them out of that buffer — rows are contiguous on disk, so a file-order scan is really one
/// sequential read, and issuing a separate read per row is what actually dominated scan time;</item>
/// <item>exposes the raw bytes, so a row can be rejected (or decoded) without a second read.</item>
/// </list>
/// The raw path is only offered for an unedited base row, whose file bytes are exactly what the user
/// sees; anything else falls back to <see cref="RowResolver"/>.
///
/// Not thread-safe by design: each worker constructs its own, which is what lets the buffers be
/// reused without synchronization.
/// </summary>
internal sealed class ScanRowReader(FileIndex fileIndex, IOverlayView overlay, DecodedRowCache? cache, bool rowsInFileOrder) : IDisposable
{
    /// <summary>Starting size of the rented row buffer; grown on demand for a longer row.</summary>
    private const int InitialRowBufferBytes = 8 * 1024;

    /// <summary>
    /// How much of the file to pull in at a time when walking rows in file order. Rows are
    /// contiguous on disk, so reading in runs this size turns roughly 25,000 individual reads into one.
    /// </summary>
    internal const int ScanChunkBytes = 1024 * 1024;

    private byte[] _rowBuffer = ArrayPool<byte>.Shared.Rent(InitialRowBufferBytes);
    private byte[]? _chunk = rowsInFileOrder ? ArrayPool<byte>.Shared.Rent(ScanChunkBytes) : null;
    private long _chunkStart = -1;
    private int _chunkLength;

    /// <summary>
    /// Whether a range of a row list runs in ascending (file) order — true for an unsorted or
    /// "_ID"-sorted view, false once an arbitrary-column sort has shuffled it. Tells the reader
    /// whether reading ahead in file-sized runs will pay off or just thrash.
    /// </summary>
    public static bool IsAscending(IReadOnlyList<long> rowIndices, int start, int end)
    {
        for (int i = start + 1; i < end; i++)
        {
            if (rowIndices[i] < rowIndices[i - 1]) return false;
        }
        return true;
    }

    public bool TryReadRawRow(long rowIndex, out ReadOnlySpan<byte> raw)
    {
        raw = default;
        if (rowIndex < 0) return false;                                  // added/duplicated row — no file bytes
        if (!overlay.IsEmpty && overlay.TryGetCellEdits(rowIndex, out _)) return false; // edited — bytes are stale

        (long offset, int length) = fileIndex.GetRowExtent(rowIndex);

        if (_chunk is not null && length <= _chunk.Length)
        {
            // Refill only when moving forward: a backwards jump means this isn't a file-order walk
            // after all, and re-reading a whole run for one row would cost more than the single
            // read below.
            if (_chunkStart < 0 || offset >= _chunkStart + _chunkLength)
            {
                _chunkStart = offset;
                _chunkLength = fileIndex.ReadBytes(_chunk, offset);
            }

            if (offset >= _chunkStart && offset + length <= _chunkStart + _chunkLength)
            {
                raw = new ReadOnlySpan<byte>(_chunk, (int)(offset - _chunkStart), length);
                return true;
            }
        }

        if (length > _rowBuffer.Length) Grow(length);

        int read = fileIndex.TryReadRowBytes(rowIndex, _rowBuffer);
        if (read < 0) return false;
        raw = new ReadOnlySpan<byte>(_rowBuffer, 0, read);
        return true;
    }

    /// <summary>Splits bytes already read by <see cref="TryReadRawRow"/> into field values — no second read, and no overlay work (the raw path is only offered for rows that have none).</summary>
    public string[] ParseFields(ReadOnlySpan<byte> raw) =>
        DifRowParser.ParseRow(raw, (byte)fileIndex.Header.Delimiter, DifFormatOptions.TextEncoding);

    /// <summary>
    /// A row's field values, or null if it is a tombstone. Without a cache to consult (a large scan,
    /// where filling one would evict the rows the visible page needs anyway) an unedited row goes
    /// through the same read-ahead buffer the raw path uses, instead of a read of its own; with a
    /// cache (a small set, which a repeat pass is likely to revisit) the cached decode wins.
    /// </summary>
    public IReadOnlyList<string>? GetFields(long rowIndex)
    {
        if (cache is null && TryReadRawRow(rowIndex, out ReadOnlySpan<byte> raw))
        {
            return ParseFields(raw);
        }
        return RowResolver.Resolve(rowIndex, fileIndex, overlay, cache)?.FieldValues;
    }

    private void Grow(int required)
    {
        ArrayPool<byte>.Shared.Return(_rowBuffer);
        _rowBuffer = ArrayPool<byte>.Shared.Rent(required);
    }

    public void Dispose()
    {
        ArrayPool<byte>.Shared.Return(_rowBuffer);
        if (_chunk is not null) ArrayPool<byte>.Shared.Return(_chunk);
    }
}
