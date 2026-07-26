using System.IO.MemoryMappedFiles;
using FileViewer.Core.Dif;
using FileViewer.Core.Native;
using FileViewer.Core.Sorting;

namespace FileViewer.Core.Indexing;

/// <summary>
/// Opens a DIF file over a read-only memory mapping and builds its <see cref="FileIndex"/>.
///
/// Phase A (sequential, cheap): locates the header/field/trailer markers via
/// <see cref="DifHeaderParser"/> without touching the bulk data-row region.
///
/// Phase B (parallel): scans only the <c>[DataStartOffset, DataEndOffsetExclusive)</c> byte range
/// in contiguous, line-aligned chunks — one <see cref="Task"/> per chunk — extracting each row's
/// offset/length and sort key into per-chunk unmanaged buffers, then merges those buffers into the
/// shared <see cref="UnmanagedArray{T}"/>s in chunk order (which, since chunks are contiguous and
/// ordered, reproduces file order without any offset-based re-sort).
/// </summary>
public static class FileIndexer
{
    /// <summary>Minimum bytes per chunk before Phase B bothers splitting further — avoids pointless parallelism overhead on small files.</summary>
    private const long MinChunkBytes = 4 * 1024 * 1024;

    private const long ProgressReportRowInterval = 50_000;

    public static Task<FileIndex> IndexAsync(
        string path, IProgress<IndexingProgress>? progress = null, CancellationToken cancellationToken = default)
    {
        return Task.Run(() => IndexCore(path, progress, cancellationToken), cancellationToken);
    }

    private static unsafe FileIndex IndexCore(string path, IProgress<IndexingProgress>? progress, CancellationToken cancellationToken)
    {
        long fileLength = new FileInfo(path).Length;

        var mappedFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        MemoryMappedViewAccessor? accessor = null;
        byte* pointer = null;
        bool ownershipTransferred = false;
        try
        {
            accessor = fileLength == 0
                ? mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read)
                : mappedFile.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);

            byte* rawPointer = null;
            accessor.SafeMemoryMappedViewHandle.AcquirePointer(ref rawPointer);
            pointer = rawPointer + accessor.PointerOffset;

            var diagnostics = new List<DifDiagnostic>();

            int headWindowLength = checked((int)Math.Min(fileLength, DifHeaderParser.HeadWindowBytes));
            var headWindow = new ReadOnlySpan<byte>(pointer, headWindowLength);
            var forwardResult = DifHeaderParser.ParseHeaderAndFields(headWindow, diagnostics);

            if (forwardResult is null)
            {
                DifFileHeader invalidHeader = DifHeaderParser.CreateInvalidHeader(diagnostics);
                var invalidIndex = new FileIndex(path, fileLength, mappedFile, accessor, pointer, invalidHeader,
                    new UnmanagedArray<RowIndexEntry>(1), new UnmanagedArray<SortKey>(1), diagnostics);
                ownershipTransferred = true;
                return invalidIndex;
            }

            (Dictionary<string, string> headerMetadata, List<string> columnNames, long dataStartOffset) = forwardResult.Value;
            char delimiter = DifHeaderParser.ResolveDelimiter(headerMetadata, diagnostics);

            long tailWindowStart = DifHeaderParser.ComputeTailWindowStart(dataStartOffset, fileLength);
            int tailWindowLength = checked((int)(fileLength - tailWindowStart));
            var tailWindow = new ReadOnlySpan<byte>(pointer + tailWindowStart, tailWindowLength);
            (Dictionary<string, string> trailerMetadata, long dataEndOffsetExclusive) =
                DifHeaderParser.ParseTrailerAndDataEnd(tailWindow, tailWindowStart, fileLength, diagnostics);

            int? declaredDataRecords = null;
            if (trailerMetadata.TryGetValue(DifFormatOptions.DataRecordsKey, out string? recordsText)
                && int.TryParse(recordsText, out int parsedRecords))
            {
                declaredDataRecords = parsedRecords;
            }

            var header = new DifFileHeader
            {
                HeaderMetadata = headerMetadata,
                Delimiter = delimiter,
                ColumnNames = columnNames,
                TrailerMetadata = trailerMetadata,
                DeclaredDataRecords = declaredDataRecords,
                DataStartOffset = dataStartOffset,
                DataEndOffsetExclusive = dataEndOffsetExclusive,
                IsValid = true,
                Diagnostics = diagnostics,
            };

            int idColumnIndex = header.ColumnIndexOf("_ID");

            (UnmanagedArray<RowIndexEntry> rowIndex, UnmanagedArray<SortKey> sortKeys, List<DifDiagnostic> rowDiagnostics) = ScanDataRegion(
                pointer, dataStartOffset, dataEndOffsetExclusive, (byte)delimiter, columnNames.Count, idColumnIndex,
                fileLength, progress, cancellationToken);

            diagnostics.AddRange(rowDiagnostics);

            if (declaredDataRecords is int declared && declared != checked((int)rowIndex.Count))
            {
                diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                    $"Trailer declares {DifFormatOptions.DataRecordsKey}={declared} but {rowIndex.Count} row(s) were actually indexed.",
                    dataEndOffsetExclusive));
            }

            var result = new FileIndex(path, fileLength, mappedFile, accessor, pointer, header, rowIndex, sortKeys, diagnostics);
            ownershipTransferred = true;
            return result;
        }
        finally
        {
            if (!ownershipTransferred)
            {
                if (pointer != null)
                {
                    accessor!.SafeMemoryMappedViewHandle.ReleasePointer();
                }
                accessor?.Dispose();
                mappedFile.Dispose();
            }
        }
    }

    private static unsafe (UnmanagedArray<RowIndexEntry> RowIndex, UnmanagedArray<SortKey> SortKeys, List<DifDiagnostic> Diagnostics) ScanDataRegion(
        byte* filePointer,
        long dataStartOffset,
        long dataEndOffsetExclusive,
        byte delimiter,
        int expectedColumnCount,
        int idColumnIndex,
        long totalFileLength,
        IProgress<IndexingProgress>? progress,
        CancellationToken cancellationToken)
    {
        long dataLength = dataEndOffsetExclusive - dataStartOffset;
        int chunkCount = dataLength <= 0
            ? 1
            : (int)Math.Max(1, Math.Min(Environment.ProcessorCount, dataLength / MinChunkBytes));

        long[] boundaries = ComputeChunkBoundaries(filePointer, dataStartOffset, dataEndOffsetExclusive, chunkCount);

        var chunkRowIndexes = new UnmanagedArray<RowIndexEntry>[boundaries.Length - 1];
        var chunkSortKeys = new UnmanagedArray<SortKey>[boundaries.Length - 1];
        var chunkDiagnostics = new List<DifDiagnostic>[boundaries.Length - 1];

        var accumulator = new ProgressAccumulator(totalFileLength, progress);

        var tasks = new Task[boundaries.Length - 1];
        for (int i = 0; i < tasks.Length; i++)
        {
            int chunkIndex = i;
            tasks[i] = Task.Run(() =>
            {
                var localRowIndex = new UnmanagedArray<RowIndexEntry>();
                var localSortKeys = new UnmanagedArray<SortKey>();
                var localDiagnostics = ScanChunk(
                    filePointer, boundaries[chunkIndex], boundaries[chunkIndex + 1],
                    delimiter, expectedColumnCount, idColumnIndex,
                    localRowIndex, localSortKeys, accumulator, cancellationToken);

                chunkRowIndexes[chunkIndex] = localRowIndex;
                chunkSortKeys[chunkIndex] = localSortKeys;
                chunkDiagnostics[chunkIndex] = localDiagnostics;
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
        var mergedDiagnostics = new List<DifDiagnostic>();

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

            mergedDiagnostics.AddRange(chunkDiagnostics[i]);

            chunkRowIndexes[i].Dispose();
            chunkSortKeys[i].Dispose();
        }

        return (mergedRowIndex, mergedSortKeys, mergedDiagnostics);
    }

    /// <summary>
    /// Computes <paramref name="chunkCount"/> + 1 boundary offsets bounding [dataStartOffset,
    /// dataEndOffsetExclusive) such that every boundary except the first and last falls exactly
    /// after a '\n' — i.e. each chunk's range starts and ends on a line boundary, so no line is
    /// split across chunks and none is double-counted.
    /// </summary>
    private static unsafe long[] ComputeChunkBoundaries(byte* filePointer, long dataStartOffset, long dataEndOffsetExclusive, int chunkCount)
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

            // Search forward from the nominal boundary for the next '\n'; the byte right after it
            // is the first byte of a new line, which becomes this chunk's actual start.
            long searchStart = nominal;
            long remaining = dataEndOffsetExclusive - searchStart;
            var searchSpan = new ReadOnlySpan<byte>(filePointer + searchStart, checked((int)remaining));
            int relativeNewLine = searchSpan.IndexOf((byte)'\n');

            long actualBoundary = relativeNewLine < 0 ? dataEndOffsetExclusive : searchStart + relativeNewLine + 1;
            boundaries[i] = actualBoundary;
            previousBoundary = actualBoundary;
        }

        return boundaries;
    }

    private static unsafe List<DifDiagnostic> ScanChunk(
        byte* filePointer,
        long chunkStart,
        long chunkEnd,
        byte delimiter,
        int expectedColumnCount,
        int idColumnIndex,
        UnmanagedArray<RowIndexEntry> outRowIndex,
        UnmanagedArray<SortKey> outSortKeys,
        ProgressAccumulator accumulator,
        CancellationToken cancellationToken)
    {
        var diagnostics = new List<DifDiagnostic>();
        int chunkLength = checked((int)(chunkEnd - chunkStart));
        if (chunkLength <= 0) return diagnostics;

        var chunkSpan = new ReadOnlySpan<byte>(filePointer + chunkStart, chunkLength);

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

            int actualFieldCount = DifRowParser.CountFields(trimmed, delimiter);
            if (actualFieldCount != expectedColumnCount)
            {
                diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                    $"Row at offset {absoluteOffset} has {actualFieldCount} field(s), expected {expectedColumnCount}; row is still indexed.",
                    absoluteOffset));
            }

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

        return diagnostics;
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
