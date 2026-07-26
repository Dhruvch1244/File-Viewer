using System.Globalization;

namespace FileViewer.Core.Sorting;

/// <summary>Compares two field values numerically if both parse as a number, otherwise falls back to ordinal string comparison — makes column-header sort behave sensibly on numeric columns (PRICE, VOLUME, ...) without needing per-column type metadata.</summary>
internal sealed class NumericAwareStringComparer : IComparer<string>
{
    public static readonly NumericAwareStringComparer Instance = new();

    public int Compare(string? x, string? y)
    {
        x ??= string.Empty;
        y ??= string.Empty;

        if (double.TryParse(x, NumberStyles.Float, CultureInfo.InvariantCulture, out double xNumber)
            && double.TryParse(y, NumberStyles.Float, CultureInfo.InvariantCulture, out double yNumber))
        {
            return xNumber.CompareTo(yNumber);
        }

        return string.CompareOrdinal(x, y);
    }
}
