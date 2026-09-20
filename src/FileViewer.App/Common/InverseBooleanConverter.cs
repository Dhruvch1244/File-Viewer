using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace FileViewer.App;

/// <summary>Negates a bool — for the "enabled while not busy" half of a pair of controls.</summary>
public sealed class InverseBooleanConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => value is not true;
}

/// <summary>Shows an element while a bool is false — the counterpart to <see cref="System.Windows.Controls.BooleanToVisibilityConverter"/>, for swapping one control for another.</summary>
public sealed class InverseBooleanToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is true ? Visibility.Collapsed : Visibility.Visible;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
