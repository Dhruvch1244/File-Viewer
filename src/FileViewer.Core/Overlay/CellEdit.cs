namespace FileViewer.Core.Overlay;

public sealed record CellEdit(long RowIndex, string Column, string NewValue);
