using FileViewer.Core.Caching;

namespace FileViewer.Core.Overlay;

/// <summary>A row's final field values after applying the edit overlay — what render, export, and preview all consume.</summary>
public sealed record ResolvedRow(long RowIndex, IReadOnlyList<string> FieldValues, RowRenderState RenderState);
