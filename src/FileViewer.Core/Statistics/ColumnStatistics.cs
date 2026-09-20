using System.Globalization;

namespace FileViewer.Core.Statistics;

/// <summary>
/// What one column actually contains, across the rows currently in view: how many rows have a value
/// at all, how many distinct values there are, the extremes, and — when the values parse as numbers
/// — the numeric summary too.
///
/// The point is to answer "is this column worth filtering on, and what am I looking at" before
/// opening a filter menu: a column with one distinct value is noise, a column with a value on 3% of
/// rows is probably the one you want.
/// </summary>
/// <param name="ColumnName">The column these numbers describe.</param>
/// <param name="RowCount">Rows examined (after any filters already applied, excluding deleted rows).</param>
/// <param name="BlankCount">Rows whose value is empty.</param>
/// <param name="DistinctCount">Distinct values seen, up to <paramref name="DistinctTruncated"/>'s cap.</param>
/// <param name="DistinctTruncated">True if the column has more distinct values than were counted.</param>
/// <param name="Min">Lowest non-blank value, compared the way the grid sorts (numerically when both sides are numbers).</param>
/// <param name="Max">Highest non-blank value, same comparison.</param>
/// <param name="NumericCount">How many non-blank values parsed as numbers.</param>
/// <param name="NumericMin">Smallest numeric value, if any.</param>
/// <param name="NumericMax">Largest numeric value, if any.</param>
/// <param name="Sum">Sum of the numeric values, if any.</param>
public sealed record ColumnStatistics(
    string ColumnName,
    int RowCount,
    int BlankCount,
    int DistinctCount,
    bool DistinctTruncated,
    string? Min,
    string? Max,
    int NumericCount,
    double? NumericMin,
    double? NumericMax,
    double? Sum)
{
    /// <summary>Rows that have a value in this column.</summary>
    public int NonBlankCount => RowCount - BlankCount;

    /// <summary>Mean of the numeric values, or null if none parsed as numbers.</summary>
    public double? Mean => NumericCount > 0 && Sum is double sum ? sum / NumericCount : null;

    /// <summary>
    /// True when every non-blank value parsed as a number — i.e. treating this column as numeric is
    /// safe, rather than a guess made from a sample.
    /// </summary>
    public bool IsFullyNumeric => NonBlankCount > 0 && NumericCount == NonBlankCount;

    /// <summary>Share of examined rows that are blank, as a fraction (0–1).</summary>
    public double BlankFraction => RowCount == 0 ? 0 : (double)BlankCount / RowCount;

    public static ColumnStatistics Empty(string columnName) =>
        new(columnName, 0, 0, 0, false, null, null, 0, null, null, null);

    /// <summary>Formats a numeric summary value the way the UI shows it — trimmed of trailing zeros, thousands-separated.</summary>
    public static string FormatNumber(double value) =>
        Math.Abs(value) >= 1e12 || (value != 0 && Math.Abs(value) < 1e-4)
            ? value.ToString("0.####e+0", CultureInfo.InvariantCulture)
            : value.ToString("#,0.####", CultureInfo.InvariantCulture);
}
