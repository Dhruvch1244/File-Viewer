using System.Globalization;
using System.IO;
using System.Windows.Data;

namespace FileViewer.App;

/// <summary>
/// Shows just the file name of a full path. Used where both are worth showing — the recent-files
/// list puts the name on one line and the path underneath, because exports frequently share a name
/// and differ only by folder or date.
/// </summary>
public sealed class FileNameConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is string path && path.Length > 0 ? Path.GetFileName(path) : string.Empty;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
