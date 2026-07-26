namespace FileViewer.Core.Caching;

public enum RowRenderState
{
    Normal,
    Added,
    Duplicated,
    Edited,
    Malformed,
}

/// <summary>A row's materialized field values, ready to display/edit/export. Deleted rows resolve to a tombstone, never a <see cref="DecodedRow"/>.</summary>
public sealed record DecodedRow(long RowIndex, IReadOnlyList<string> FieldValues, RowRenderState RenderState);
