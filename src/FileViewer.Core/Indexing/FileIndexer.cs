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

        MemoryMappedFile mappedFile;
        try
        {
            mappedFile = MemoryMappedFile.CreateFromFile(path, FileMode.Open, mapName: null, capacity: 0, MemoryMappedFileAccess.Read);
        }
        catch (IOException ex) when (IsInsufficientMemoryError(ex))
        {
            throw new IOException(BuildInsufficientMemoryMessage(fileLength, ex), ex);
        }

        MemoryMappedViewAccessor? accessor = null;
        byte* pointer = null;
        bool ownershipTransferred = false;
        try
        {
            try
            {
                accessor = fileLength == 0
                    ? mappedFile.CreateViewAccessor(0, 0, MemoryMappedFileAccess.Read)
                    : mappedFile.CreateViewAccessor(0, fileLength, MemoryMappedFileAccess.Read);
            }
            catch (IOException ex) when (IsInsufficientMemoryError(ex))
            {
                throw new IOException(BuildInsufficientMemoryMessage(fileLength, ex), ex);
            }

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

            (string headerMarker, bool hasFileStartMarker, Dictionary<string, string> headerMetadata,
                List<string> columnNames, Dictionary<string, string> postFieldsMetadata, long dataStartOffset) = forwardResult.Value;
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
                pointer, dataStartOffset, dataEndOffsetExclusive, (byte)delimiter, idColumnIndex,
                fileLength, progress, cancellationToken);

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

    /// <summary>Win32 ERROR_NOT_ENOUGH_MEMORY (8), as the HRESULT an IOException carries when Windows refuses a file mapping.</summary>
    private const int ErrorNotEnoughMemoryHResult = unchecked((int)0x80070008);

    /// <summary>
    /// True for the specific low-level failure this file exists to translate: Windows refusing to
    /// create or map a memory-mapped view because it can't satisfy the request against available
    /// memory/commit — reported as an <see cref="IOException"/> whose message is the bare, unhelpful
    /// OS string "Not enough memory resources are available to process this command." Matches by
    /// HResult first (reliable on Windows) and falls back to the message text so the friendlier error
    /// below still applies if a different runtime/OS wraps the same underlying failure differently.
    /// </summary>
    internal static bool IsInsufficientMemoryError(IOException ex) =>
        ex.HResult == ErrorNotEnoughMemoryHResult
        || ex.Message.Contains("not enough memory", StringComparison.OrdinalIgnoreCase);

    /// <summary>Rough size this app is built and tested against (see README/PRS) — used only to phrase <see cref="BuildInsufficientMemoryMessage"/> accurately, not to reject anything.</summary>
    private const long TestedFileSizeBytes = 2L * 1024 * 1024 * 1024;

    /// <summary>
    /// Opening a file requires mapping it into one contiguous view up front (<see cref="IndexCore"/>
    /// reads/scans directly off that pointer for the file's entire lifetime — see the class remarks).
    /// A file well beyond this app's tested ~2 GB target can fail here simply because the machine
    /// doesn't have enough free memory/page-file space to back a view that size. But the same failure
    /// can also hit a much smaller file — mapping is an all-or-nothing, whole-file commitment, so a
    /// machine that's already low on memory/page-file space for unrelated reasons (other running
    /// applications, a small fixed page file, etc.) can fail here too. The message has to say which
    /// situation actually applies instead of always blaming file size — telling someone their 1.1 GB
    /// file is "well beyond 2 GB" is simply wrong and sends them chasing the wrong fix.
    /// </summary>
    internal static string BuildInsufficientMemoryMessage(long fileLength, IOException originalError)
    {
        double fileGb = fileLength / (1024.0 * 1024.0 * 1024.0);
        double availableGb = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024.0 * 1024.0);

        string sizeContext = fileLength > TestedFileSizeBytes
            ? "This is well beyond the ~2 GB file size Bloomberg File Viewer is built and tested for."
            : "This is within the ~2 GB file size Bloomberg File Viewer is built and tested for, so " +
              "the failure points to this machine's available memory (RAM + page file) being " +
              "unusually constrained right now — not the file's size.";

        return $"This file is about {fileGb:N1} GB, and this machine currently reports about " +
               $"{availableGb:N1} GB of memory available to this process. Opening a file requires " +
               "mapping the whole file into memory at once, and this machine could not satisfy that " +
               $"request (Windows reported: \"{originalError.Message}\"). {sizeContext} Try increasing " +
               "the Windows page file (virtual memory) size, closing other memory-heavy applications, " +
               "or opening a smaller file.";
    }

    private static unsafe (UnmanagedArray<RowIndexEntry> RowIndex, UnmanagedArray<SortKey> SortKeys) ScanDataRegion(
        byte* filePointer,
        long dataStartOffset,
        long dataEndOffsetExclusive,
        byte delimiter,
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
                    filePointer, boundaries[chunkIndex], boundaries[chunkIndex + 1],
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

    /// <summary>
    /// Indexes every row in [chunkStart, chunkEnd) — offset/length plus sort key. Deliberately does
    /// not check each row's field count against the header's declared column count: a
    /// content-shape mismatch isn't a reason to flag or refuse a row, only to fail file-open
    /// entirely (missing structural markers) is handled by DifHeaderParser. Every row is indexed
    /// and displayed as-is.
    /// </summary>
    private static unsafe void ScanChunk(
        byte* filePointer,
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
