using FileViewer.Core.Caching;
using FileViewer.Core.Dif;
using FileViewer.Core.Indexing;

namespace FileViewer.Core.Overlay;

/// <summary>
/// The single row-resolution path shared by rendering, export, and preview — resolving a row any
/// other way risks exactly the drift the PRS §12 risk (edit-on-a-since-deleted-row, preview not
/// matching real export, etc.) is meant to guard against.
///
/// Resolution order:
/// 1. Deleted rows short-circuit to a tombstone (null) before any cell edits are even looked at —
///    a deleted row's stored edits are retained but inert, and reappear automatically if the row
///    is later restored (this is the concrete answer to "what happens when you edit a row that's
///    since been deleted": nothing, until/unless it comes back).
/// 2. Added/Duplicated rows (negative row index) read their template from the overlay — there is
///    no backing file content.
/// 3. Existing rows are decoded from the mapped file (through <see cref="DecodedRowCache"/>, which
///    caches the row's raw file-decoded fields — never the post-overlay result, since overlay
///    state can change without the underlying file bytes changing).
/// 4. Cell edits apply on top, in insertion order — last edit to a given column wins.
///
/// A row's actual field count is never checked against the header's declared column count here —
/// every row is shown exactly as it decodes, with no "this record looks malformed" judgment call.
/// </summary>
public static class RowResolver
{
    /// <summary>Resolves a row, or returns null if it is a tombstone (deleted — never rendered/exported).</summary>
    public static ResolvedRow? Resolve(long rowIndex, FileIndex fileIndex, EditOverlay overlay, DecodedRowCache cache)
    {
        RowState state = overlay.GetRowState(rowIndex);
        if (state == RowState.Deleted)
        {
            return null;
        }

        IReadOnlyList<string> baseFields;

        if (rowIndex < 0)
        {
            baseFields = overlay.AddedRowData.TryGetValue(rowIndex, out string[]? template) ? template : [];
        }
        else if (cache.TryGet(rowIndex, out DecodedRow? cached))
        {
            baseFields = cached.FieldValues;
        }
        else
        {
            ReadOnlySpan<byte> rowBytes = fileIndex.GetRowBytes(rowIndex);
            string[] parsed = DifRowParser.ParseRow(rowBytes, (byte)fileIndex.Header.Delimiter, DifFormatOptions.TextEncoding);
            baseFields = parsed;
            cache.Set(rowIndex, new DecodedRow(rowIndex, parsed));
        }

        string[] resolvedFields = [.. baseFields]; // defensive copy — never mutate a cached/template array in place

        bool hasEdits = false;
        if (overlay.CellEdits.TryGetValue(rowIndex, out List<CellEdit>? edits))
        {
            foreach (CellEdit edit in edits)
            {
                int columnIndex = fileIndex.Header.ColumnIndexOf(edit.Column);
                if (columnIndex >= 0 && columnIndex < resolvedFields.Length)
                {
                    resolvedFields[columnIndex] = edit.NewValue;
                    hasEdits = true;
                }
            }
        }

        RowRenderState renderState = state switch
        {
            RowState.Added => RowRenderState.Added,
            RowState.Duplicated => RowRenderState.Duplicated,
            _ when hasEdits => RowRenderState.Edited,
            _ => RowRenderState.Normal,
        };

        return new ResolvedRow(rowIndex, resolvedFields, renderState);
    }
}
