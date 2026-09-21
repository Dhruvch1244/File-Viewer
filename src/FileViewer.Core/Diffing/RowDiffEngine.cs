using FileViewer.Core.Caching;
using FileViewer.Core.Export;
using FileViewer.Core.Indexing;
using FileViewer.Core.Overlay;

namespace FileViewer.Core.Diffing;

/// <summary>
/// Compares two sides — two sections, possibly of two different files — row by row, matched on a
/// key column rather than by position: row order between two exports of "the same" data routinely
/// differs even when the data itself does not, so comparing position N of one against position N
/// of the other would report a file as almost entirely changed the moment a single row moved.
///
/// Both sides are resolved through <see cref="RowResolver"/>, the same path rendering/export use, so
/// the comparison sees whatever the user currently sees: overlay edits, added/duplicated rows, and
/// deleted rows dropped, all exactly as they'd export.
/// </summary>
public static class RowDiffEngine
{
    /// <summary>
    /// Compares every live row of <paramref name="leftIndex"/> against every live row of
    /// <paramref name="rightIndex"/>, matched by <paramref name="keyColumn"/>'s value. Columns
    /// compared are the ones both sides declare (by name); a column only one side has plays no part
    /// in deciding <see cref="RowDiffKind.Changed"/> — there's nothing on the other side to compare
    /// it to.
    /// </summary>
    public static RowDiffResult Compare(
        FileIndex leftIndex,
        EditOverlay leftOverlay,
        DecodedRowCache? leftCache,
        FileIndex rightIndex,
        EditOverlay rightOverlay,
        DecodedRowCache? rightCache,
        string keyColumn,
        CancellationToken cancellationToken = default)
    {
        int leftKeyColumn = leftIndex.Header.ColumnIndexOf(keyColumn);
        int rightKeyColumn = rightIndex.Header.ColumnIndexOf(keyColumn);
        bool keyMissingLeft = leftKeyColumn < 0;
        bool keyMissingRight = rightKeyColumn < 0;

        List<string> comparedColumns = [.. leftIndex.Header.ColumnNames.Intersect(rightIndex.Header.ColumnNames, StringComparer.Ordinal)];

        if (keyMissingLeft || keyMissingRight)
        {
            return new RowDiffResult([], 0, 0, 0, 0, comparedColumns, keyMissingLeft, keyMissingRight, false, false);
        }

        // Resolved once, up front: every row of both sides gets its comparedColumns' left/right
        // indices looked up once here rather than once per row further down.
        var columnIndexPairs = new (int Left, int Right)[comparedColumns.Count];
        for (int i = 0; i < comparedColumns.Count; i++)
        {
            columnIndexPairs[i] = (leftIndex.Header.ColumnIndexOf(comparedColumns[i]), rightIndex.Header.ColumnIndexOf(comparedColumns[i]));
        }

        // Index the right side by key first — the left walk then does one dictionary probe per row
        // instead of an O(rows-left * rows-right) nested scan.
        var rightByKey = new Dictionary<string, long>(StringComparer.Ordinal);
        bool duplicateRight = false;
        foreach (long rowIndex in ExportRunner.FileOrderWithAddedRows(rightIndex, rightOverlay))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedRow? resolved = RowResolver.Resolve(rowIndex, rightIndex, rightOverlay, rightCache);
            if (resolved is null) continue; // tombstone

            string key = FieldOrEmpty(resolved, rightKeyColumn);
            if (rightByKey.ContainsKey(key)) duplicateRight = true;
            rightByKey[key] = rowIndex; // last occurrence wins — flagged above, not hidden
        }

        var entries = new List<RowDiffEntry>();
        var matchedRightKeys = new HashSet<string>(StringComparer.Ordinal);
        var seenLeftKeys = new HashSet<string>(StringComparer.Ordinal);
        bool duplicateLeft = false;
        int added = 0, removed = 0, changed = 0, unchanged = 0;

        foreach (long rowIndex in ExportRunner.FileOrderWithAddedRows(leftIndex, leftOverlay))
        {
            cancellationToken.ThrowIfCancellationRequested();
            ResolvedRow? resolved = RowResolver.Resolve(rowIndex, leftIndex, leftOverlay, leftCache);
            if (resolved is null) continue;

            string key = FieldOrEmpty(resolved, leftKeyColumn);
            if (!seenLeftKeys.Add(key)) duplicateLeft = true;

            if (!rightByKey.TryGetValue(key, out long rightRowIndex))
            {
                entries.Add(new RowDiffEntry(key, rowIndex, null, RowDiffKind.Removed, []));
                removed++;
                continue;
            }

            matchedRightKeys.Add(key);
            ResolvedRow? rightResolved = RowResolver.Resolve(rightRowIndex, rightIndex, rightOverlay, rightCache);
            if (rightResolved is null)
            {
                // No concurrent mutation is expected mid-comparison, but a matched row turning out
                // to be a tombstone should still be handled rather than crash.
                entries.Add(new RowDiffEntry(key, rowIndex, null, RowDiffKind.Removed, []));
                removed++;
                continue;
            }

            List<ColumnDiff>? diffs = null;
            foreach ((string column, (int li, int ri)) in comparedColumns.Zip(columnIndexPairs))
            {
                string leftValue = FieldOrEmpty(resolved, li);
                string rightValue = FieldOrEmpty(rightResolved, ri);
                if (!string.Equals(leftValue, rightValue, StringComparison.Ordinal))
                {
                    diffs ??= [];
                    diffs.Add(new ColumnDiff(column, leftValue, rightValue));
                }
            }

            if (diffs is { Count: > 0 })
            {
                entries.Add(new RowDiffEntry(key, rowIndex, rightRowIndex, RowDiffKind.Changed, diffs));
                changed++;
            }
            else
            {
                unchanged++;
            }
        }

        foreach ((string key, long rightRowIndex) in rightByKey)
        {
            if (matchedRightKeys.Contains(key)) continue;
            entries.Add(new RowDiffEntry(key, null, rightRowIndex, RowDiffKind.Added, []));
            added++;
        }

        return new RowDiffResult(entries, added, removed, changed, unchanged, comparedColumns, false, false, duplicateLeft, duplicateRight);
    }

    private static string FieldOrEmpty(ResolvedRow row, int columnIndex) =>
        columnIndex >= 0 && columnIndex < row.FieldValues.Count ? row.FieldValues[columnIndex] : string.Empty;
}
