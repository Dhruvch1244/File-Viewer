using Microsoft.Win32.SafeHandles;
using FileViewer.Core.Dif;
using FileViewer.Core.Native;
using FileViewer.Core.Sorting;

namespace FileViewer.Core.Indexing;

/// <summary>
/// Opens a DIF file over a read-only file handle and builds its <see cref="FileIndex"/>. Never maps
/// the whole file into memory at once — see <see cref="FileIndex"/>'s remarks for why that matters.
///
/// Phase A (sequential, cheap): locates the header/field/trailer markers via
/// <see cref="DifHeaderParser"/> by reading two small bounded windows (a head window and a tail
/// window), without touching the bulk data-row region.
///
/// Phase B (parallel): scans only the <c>[DataStartOffset, DataEndOffsetExclusive)</c> byte range
/// in contiguous, line-aligned chunks — one <see cref="Task"/> per chunk, each chunk capped at
/// <see cref="MaxChunkReadBytes"/> so no single read (and therefore no single buffer) scales with
/// file size — extracting each row's offset/length and sort key into per-chunk unmanaged buffers,
/// then merges those buffers into the shared <see cref="UnmanagedArray{T}"/>s in chunk order (which,
/// since chunks are contiguous and ordered, reproduces file order without any offset-based re-sort).
/// </summary>
public static class FileIndexer
{
    /// <summary>Minimum bytes per chunk before Phase B bothers splitting further — avoids pointless parallelism overhead on small files.</summary>
    internal const long MinChunkBytes = 4 * 1024 * 1024;

    /// <summary>
    /// Upper bound on how much any single chunk (and therefore any single read buffer) is allowed
    /// to be, regardless of file size or core count — the reason Phase B never needs anywhere close
    /// to "the whole file" in memory at once. A big file with few cores just becomes more, smaller
    /// chunks instead of fewer, larger ones; <see cref="Environment.ProcessorCount"/> already bounds
    /// how many run concurrently, so peak Phase B memory stays roughly ProcessorCount × this value
    /// no matter how large the file is.
    /// </summary>
    internal const int MaxChunkReadBytes = 64 * 1024 * 1024;

    /// <summary>Size of the bounded look-ahead window used to find the next line boundary near a candidate chunk split point — comfortably larger than any real DIF line.</summary>
    private const int LineBoundarySearchWindowBytes = 1024 * 1024;

    private const long ProgressReportRowInterval = 50_000;

    public static Task<FileIndex> IndexAsync(
        string path, IProgress<IndexingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => IndexCore(path, progress, cancellationToken), cancellationToken);
    }

    private static FileIndex IndexCore(string path, IProgress<IndexingProgress>? progress, CancellationToken cancellationToken)
    {
        long fileLength = new FileInfo(path).Length;
        SafeFileHandle fileHandle = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.Read, FileOptions.RandomAccess);

        bool ownershipTransferred = false;
        try
        {
            var diagnostics = new List<DifDiagnostic>();

            int headWindowLength = checked((int)Math.Min(fileLength, DifHeaderParser.HeadWindowBytes));
            byte[] headBuffer = new byte[headWindowLength];
            RandomAccessReader.ReadExactly(fileHandle, headBuffer, 0);
            var forwardResult = DifHeaderParser.ParseHeaderAndFields(headBuffer, diagnostics);

            if (forwardResult is null)
            {
                DifFileHeader invalidHeader = DifHeaderParser.CreateInvalidHeader(diagnostics);
                var invalidIndex = new FileIndex(path, fileLength, fileHandle, invalidHeader,
                    new UnmanagedArray<RowIndexEntry>(1), new UnmanagedArray<SortKey>(1), diagnostics);
                ownershipTransferred = true;
                return invalidIndex;
            }

            (string headerMarker, bool hasFileStartMarker, Dictionary<string, string> headerMetadata,
                List<string> columnNames, Dictionary<string, string> postFieldsMetadata, long dataStartOffset) = forwardResult.Value;
            char delimiter = DifHeaderParser.ResolveDelimiter(headerMetadata, diagnostics);

            long tailWindowStart = DifHeaderParser.ComputeTailWindowStart(dataStartOffset, fileLength);
            int tailWindowLength = checked((int)(fileLength - tailWindowStart));
            byte[] tailBuffer = new byte[tailWindowLength];
            RandomAccessReader.ReadExactly(fileHandle, tailBuffer, tailWindowStart);
            (Dictionary<string, string> trailerMetadata, long dataEndOffsetExclusive) =
                DifHeaderParser.ParseTrailerAndDataEnd(tailBuffer, tailWindowStart, fileLength, diagnostics);

            int? declaredDataRecords = null;
            if (trailerMetadata.TryGetValue(DifFormatOptions.DataRecordsKey, out string? recordsText)
                && int.TryParse(recordsText, out int parsedRecords))
            {
                declaredDataRecords = parsedRecords;
            }

            var header = new DifFileHeader
            {
                HeaderMarker = headerMarker,
                HasFileStartMarker = hasFileStartMarker,
                HeaderMetadata = headerMetadata,
                Delimiter = delimiter,
                ColumnNames = columnNames,
                PostFieldsMetadata = postFieldsMetadata,
                TrailerMetadata = trailerMetadata,
                DeclaredDataRecords = declaredDataRecords,
                DataStartOffset = dataStartOffset,
                DataEndOffsetExclusive = dataEndOffsetExclusive,
                IsValid = true,
                Diagnostics = diagnostics,
            };

            int idColumnIndex = header.ColumnIndexOf("_ID");

            (UnmanagedArray<RowIndexEntry> rowIndex, UnmanagedArray<SortKey> sortKeys) = ScanDataRegion(
                fileHandle, dataStartOffset, dataEndOffsetExclusive, (byte)delimiter, idColumnIndex,
                fileLength, progress, cancellationToken);

            var result = new FileIndex(path, fileLength, fileHandle, header, rowIndex, sortKeys, diagnostics);
            ownershipTransferred = true;
            return result;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                fileHandle.Dispose();
            }
        }
    }

    private static (UnmanagedArray<RowIndexEntry> RowIndex, UnmanagedArray<SortKey> SortKeys) ScanDataRegion(
        SafeFileHandle fileHandle,
        long dataStartOffset,
        long dataEndOffsetExclusive,
        byte delimiter,
        int idColumnIndex,
        long totalFileLength,
        IProgress<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        long dataLength = dataEndOffsetExclusive - dataStartOffset;
        int chunkCount = ComputeChunkCount(dataLength);

        long[] boundaries = ComputeChunkBoundaries(fileHandle, dataStartOffset, dataEndOffsetExclusive, chunkCount);

        var chunkRowIndexes = new UnmanagedArray<RowIndexEntry>[boundaries.Length - 1];
        var chunkSortKeys = new UnmanagedArray<SortKey>[boundaries.Length - 1];

        var accumulator = new ProgressAccumulator(totalFileLength, progress);

        var tasks = new Task[boundaries.Length - 1];
        for (int i = 0; i < tasks.Length; i++)
        {
            int chunkIndex = i;
            tasks[i] = Task.Run(() =>
            {
                var localRowIndex = new UnmanagedArray<RowIndexEntry>();
                var localSortKeys = new UnmanagedArray<SortKey>();
                ScanChunk(
                    fileHandle, boundaries[chunkIndex], boundaries[chunkIndex + 1],
                    delimiter, idColumnIndex,
                    localRowIndex, localSortKeys, accumulator, cancellationToken);

                chunkRowIndexes[chunkIndex] = localRowIndex;
                chunkSortKeys[chunkIndex] = localSortKeys;
            }, cancellationToken);
        }

        try
        {
            Task.WaitAll(tasks);
        }
        catch (AggregateException ex)
        {
            foreach (var array in chunkRowIndexes) array?.Dispose();
            foreach (var array in chunkSortKeys) array?.Dispose();
            throw ex.Flatten().InnerExceptions.Count == 1 ? ex.InnerException! : ex;
        }

        nuint totalRows = 0;
        foreach (var array in chunkRowIndexes) totalRows += array.Count;
        nuint mergedInitialCapacity = totalRows == 0 ? (nuint)1 : totalRows;

        var mergedRowIndex = new UnmanagedArray<RowIndexEntry>(mergedInitialCapacity);
        var mergedSortKeys = new UnmanagedArray<SortKey>(mergedInitialCapacity);

        // Each chunk numbers its SortKey.RowIndex values locally, starting at 0 (it has no way to
        // know its global starting row position until every chunk has finished — row counts per
        // chunk aren't known up front since row length/count varies). Correct them to true global
        // row indices here, during the ordered merge, since global row index is by definition a
        // row's position in the merged RowIndexEntry array.
        long globalRowIndex = 0;
        for (int i = 0; i < chunkRowIndexes.Length; i++)
        {
            foreach (ref readonly RowIndexEntry entry in chunkRowIndexes[i].AsSpan())
            {
                mergedRowIndex.Add(entry);
            }

            long localIndex = 0;
            foreach (SortKey key in chunkSortKeys[i].AsSpan())
            {
                SortKey corrected = key;
                corrected.RowIndex = globalRowIndex + localIndex;
                mergedSortKeys.Add(corrected);
                localIndex++;
            }
            globalRowIndex += (long)chunkRowIndexes[i].Count;

            chunkRowIndexes[i].Dispose();
            chunkSortKeys[i].Dispose();
        }

        return (mergedRowIndex, mergedSortKeys);
    }

    /// <summary>
    /// At least enough chunks to keep every core busy (the original heuristic), but never fewer than
    /// needed to keep each individual chunk at or under <see cref="MaxChunkReadBytes"/> — the second
    /// condition is what actually bounds peak memory on a huge file with few cores, where the
    /// core-count heuristic alone would otherwise produce a handful of enormous chunks.
    /// </summary>
    internal static int ComputeChunkCount(long dataLength)
    {
        if (dataLength <= 0) return 1;
        int byCoreCount = (int)Math.Max(1, Math.Min(Environment.ProcessorCount, dataLength / MinChunkBytes));
        int byMaxChunkSize = checked((int)Math.Max(1, (dataLength + MaxChunkReadBytes - 1) / MaxChunkReadBytes));
        return Math.Max(byCoreCount, byMaxChunkSize);
    }

    /// <summary>
    /// Computes <paramref name="chunkCount"/> + 1 boundary offsets bounding [dataStartOffset,
    /// dataEndOffsetExclusive) such that every boundary except the first and last falls exactly
    /// after a '\n' — i.e. each chunk's range starts and ends on a line boundary, so no line is
    /// split across chunks and none is double-counted.
    /// </summary>
    private static long[] ComputeChunkBoundaries(SafeFileHandle fileHandle, long dataStartOffset, long dataEndOffsetExclusive, int chunkCount)
    {
        var boundaries = new long[chunkCount + 1];
        boundaries[0] = dataStartOffset;
        boundaries[chunkCount] = dataEndOffsetExclusive;

        long dataLength = dataEndOffsetExclusive - dataStartOffset;
        if (dataLength <= 0)
        {
            for (int i = 1; i < chunkCount; i++) boundaries[i] = dataEndOffsetExclusive;
            return boundaries;
        }

        long nominalChunkSize = dataLength / chunkCount;
        long previousBoundary = dataStartOffset;
        for (int i = 1; i < chunkCount; i++)
        {
            long nominal = dataStartOffset + i * nominalChunkSize;
            if (nominal <= previousBoundary) nominal = previousBoundary + 1;
            if (nominal >= dataEndOffsetExclusive)
            {
                boundaries[i] = dataEndOffsetExclusive;
                previousBoundary = dataEndOffsetExclusive;
                continue;
            }

            long actualBoundary = FindNextLineStart(fileHandle, nominal, dataEndOffsetExclusive);
            boundaries[i] = actualBoundary;
            previousBoundary = actualBoundary;
        }

        return boundaries;
    }

    /// <summary>
    /// Reads forward from <paramref name="searchStart"/> in bounded windows looking for the next
    /// '\n', returning the offset of the byte right after it (the first byte of the next line), or
    /// <paramref name="dataEndOffsetExclusive"/> if none is found before then. Bounded the same way
    /// <see cref="ScanChunk"/> is, for the same reason: no single read should scale with file size.
    /// </summary>
    private static long FindNextLineStart(SafeFileHandle fileHandle, long searchStart, long dataEndOffsetExclusive)
    {
        byte[] buffer = new byte[LineBoundarySearchWindowBytes];
        long pos = searchStart;
        while (pos < dataEndOffsetExclusive)
        {
            int toRead = checked((int)Math.Min(LineBoundarySearchWindowBytes, dataEndOffsetExclusive - pos));
            int read = RandomAccessReader.ReadExactly(fileHandle, buffer.AsSpan(0, toRead), pos);
            if (read == 0) break;

            int newlineIndex = buffer.AsSpan(0, read).IndexOf((byte)'\n');
            if (newlineIndex >= 0)
            {
                return pos + newlineIndex + 1;
            }

            pos += read;
            if (read < toRead) break; // EOF reached mid-read
        }
        return dataEndOffsetExclusive;
    }

    /// <summary>
    /// Indexes every row in [chunkStart, chunkEnd) — offset/length plus sort key. Deliberately does
    /// not check each row's field count against the header's declared column count: a
    /// content-shape mismatch isn't a reason to flag or refuse a row, only to fail file-open
    /// entirely (missing structural markers) is handled by DifHeaderParser. Every row is indexed
    /// and displayed as-is.
    /// </summary>
    private static void ScanChunk(
        SafeFileHandle fileHandle,
        long chunkStart,
        long chunkEnd,
        byte delimiter,
        int idColumnIndex,
        UnmanagedArray<RowIndexEntry> outRowIndex,
        UnmanagedArray<SortKey> outSortKeys,
        ProgressAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        int chunkLength = checked((int)(chunkEnd - chunkStart));
        if (chunkLength <= 0) return;

        byte[] buffer = new byte[chunkLength];
        RandomAccessReader.ReadExactly(fileHandle, buffer, chunkStart);
        var chunkSpan = new ReadOnlySpan<byte>(buffer);

        int pos = 0;
        long rowsSinceLastReport = 0;
        int posAtLastReport = 0;

        while (pos < chunkSpan.Length)
        {
            int lineStartRelative = pos;
            if (!DifLineScanner.TryReadLine(chunkSpan, ref pos, out ReadOnlySpan<byte> line)) break;

            ReadOnlySpan<byte> trimmed = DifLineScanner.TrimTrailingCr(line);
            if (trimmed.Length == 0) continue;

            long absoluteOffset = chunkStart + lineStartRelative;
            outRowIndex.Add(new RowIndexEntry(absoluteOffset, trimmed.Length));

            // Always populate a SortKey — even with idColumnIndex < 0 (no "_ID" column),
            // SortKeyBuilder produces an empty-key entry, and RowSorter's RowIndex tiebreak still
            // gives a well-defined file-order sort. This keeps SortKeys.Count == RowIndex.Count
            // unconditionally, so callers (FileViewerSession) can always treat SortKeys as "current
            // row order" without special-casing files that lack "_ID".
            //
            // RowIndex here is only this chunk's local row count — not yet the row's true global
            // index (this chunk doesn't know its global starting position until every chunk has
            // finished). ScanDataRegion's merge step corrects it.
            outSortKeys.Add(SortKeyBuilder.Build(checked((long)outRowIndex.Count - 1), trimmed, delimiter, idColumnIndex));

            rowsSinceLastReport++;
            if (rowsSinceLastReport >= ProgressReportRowInterval)
            {
                cancellationToken.ThrowIfCancellationRequested();
                accumulator.Report(rowsSinceLastReport, pos - posAtLastReport);
                rowsSinceLastReport = 0;
                posAtLastReport = pos;
            }
        }

        if (rowsSinceLastReport > 0 || pos > posAtLastReport)
        {
            accumulator.Report(rowsSinceLastReport, pos - posAtLastReport);
        }
    }

    private sealed class ProgressAccumulator(long totalBytes, IProgress<IndexingProgress>? progress)
    {
        private long _rowsFound;
        private long _bytesScanned;

        public void Report(long rowsDelta, long bytesDelta)
        {
            long rows = Interlocked.Add(ref _rowsFound, rowsDelta);
            long bytes = Interlocked.Add(ref _bytesScanned, bytesDelta);
            progress?.Report(new IndexingProgress(Math.Min(bytes, totalBytes), totalBytes, rows));
        }
    }
}
