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
    /// <summary>
    /// Resolves a row, or returns null if it is a tombstone (deleted — never rendered/exported).
    /// <paramref name="overlay"/> may be the live <see cref="EditOverlay"/> or a lock-free
    /// <see cref="OverlaySnapshot"/>; <paramref name="cache"/> may be null, which decodes the row
    /// without touching (or evicting anything from) the shared cache — what the bulk scanning paths
    /// want, since a filter pass would otherwise flush the rows the visible page needs.
    /// </summary>
    public static ResolvedRow? Resolve(long rowIndex, FileIndex fileIndex, IOverlayView overlay, DecodedRowCache? cache)
    {
        RowState state = overlay.GetRowState(rowIndex);
        if (state == RowState.Deleted)
        {
            return null;
        }

        IReadOnlyList<string> baseFields;

        if (rowIndex < 0)
        {
            baseFields = overlay.TryGetAddedRowTemplate(rowIndex, out IReadOnlyList<string> template) ? template : [];
        }
        else if (cache is not null && cache.TryGet(rowIndex, out DecodedRow? cached))
        {
            baseFields = cached.FieldValues;
        }
        else
        {
            ReadOnlySpan<byte> rowBytes = fileIndex.GetRowBytes(rowIndex);
            string[] parsed = DifRowParser.ParseRow(rowBytes, (byte)fileIndex.Header.Delimiter, DifFormatOptions.TextEncoding);
            baseFields = parsed;
            cache?.Set(rowIndex, new DecodedRow(rowIndex, parsed));
        }

        // The defensive copy only actually needs to happen once there is an edit to apply — most
        // rows in a real file never have one, and this is called for every row a filter/sort pass
        // decodes, every row a large export writes, and every row the grid renders. Copying
        // unconditionally meant paying for a full array allocation + element copy on the hot path
        // for the common case that goes on to do nothing with it. baseFields itself is never
        // mutated below: resolvedFields only ever becomes a fresh array, never baseFields cast back
        // to string[], so the original "never mutate a cached/template array in place" guarantee
        // still holds — it just isn't paid for until the first edit is actually found.
        IReadOnlyList<string> resolvedFields = baseFields;
        bool hasEdits = false;
        if (overlay.TryGetCellEdits(rowIndex, out IReadOnlyList<CellEdit> edits))
        {
            string[]? mutableFields = null;
            foreach (CellEdit edit in edits)
            {
                int columnIndex = fileIndex.Header.ColumnIndexOf(edit.Column);
                if (columnIndex >= 0 && columnIndex < baseFields.Count)
                {
                    mutableFields ??= [.. baseFields];
                    mutableFields[columnIndex] = edit.NewValue;
                    hasEdits = true;
                }
            }
            if (mutableFields is not null) resolvedFields = mutableFields;
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
