namespace FileViewer.Core.Diffing;

/// <summary>What kind of difference a <see cref="RowDiffEntry"/> represents.</summary>
public enum RowDiffKind
{
    /// <summary>Present on the right side (by key), not on the left.</summary>
    Added,

    /// <summary>Present on the left side (by key), not on the right.</summary>
    Removed,

    /// <summary>Present on both sides, with at least one compared column differing.</summary>
    Changed,
}

/// <summary>One column's differing value between the two matched rows of a <see cref="RowDiffEntry"/> of kind <see cref="RowDiffKind.Changed"/>.</summary>
public readonly record struct ColumnDiff(string Column, string LeftValue, string RightValue);

/// <summary>
/// One row that differs between the two sides being compared — a row present on only one side, or
/// one present on both whose key matched but whose compared columns did not all agree. Rows that
/// matched and agreed on every compared column are not represented individually: see
/// <see cref="RowDiffResult.UnchangedCount"/>.
/// </summary>
public sealed record RowDiffEntry(
    string Key,
    long? LeftRowIndex,
    long? RightRowIndex,
    RowDiffKind Kind,
    IReadOnlyList<ColumnDiff> ColumnDiffs);

/// <summary>
/// The result of comparing two sides by a key column: every row that differs (added, removed, or
/// changed), plus enough context to know whether the comparison itself is trustworthy — a key
/// column missing from one side, or duplicate key values on either side, both mean the match-up
/// this result is built on is not what it would be with a genuinely unique key.
/// </summary>
public sealed record RowDiffResult(
    IReadOnlyList<RowDiffEntry> Entries,
    int AddedCount,
    int RemovedCount,
    int ChangedCount,
    int UnchangedCount,
    IReadOnlyList<string> ComparedColumns,
    bool KeyColumnMissingOnLeft,
    bool KeyColumnMissingOnRight,
    bool DuplicateKeysOnLeft,
    bool DuplicateKeysOnRight)
{
    public bool IsIdentical => AddedCount == 0 && RemovedCount == 0 && ChangedCount == 0;
}
