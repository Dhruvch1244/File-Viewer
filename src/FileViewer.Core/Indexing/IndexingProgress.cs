namespace FileViewer.Core.Indexing;

public sealed record IndexingProgress(long BytesScanned, long TotalBytes, long RowsFound);
