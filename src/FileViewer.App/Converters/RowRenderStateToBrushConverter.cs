using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using FileViewer.Core.Caching;

namespace FileViewer.App.Converters;

/// <summary>Maps a row's <see cref="RowRenderState"/> to a background brush for the grid's <c>RowStyle</c> — one converter, not a per-cell multi-condition trigger, so recycled row containers stay cheap to restyle.</summary>
public sealed class RowRenderStateToBrushConverter : IValueConverter
{
    private static readonly Brush AddedBrush = new SolidColorBrush(Color.FromRgb(0xDF, 0xF5, 0xDF));
    private static readonly Brush DuplicatedBrush = new SolidColorBrush(Color.FromRgb(0xDF, 0xEA, 0xF5));
    private static readonly Brush EditedBrush = new SolidColorBrush(Color.FromRgb(0xFB, 0xF3, 0xD8));
    private static readonly Brush MalformedBrush = new SolidColorBrush(Color.FromRgb(0xF5, 0xDF, 0xDF));

    static RowRenderStateToBrushConverter()
    {
        AddedBrush.Freeze();
        DuplicatedBrush.Freeze();
        EditedBrush.Freeze();
        MalformedBrush.Freeze();
    }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        RowRenderState.Added => AddedBrush,
        RowRenderState.Duplicated => DuplicatedBrush,
        RowRenderState.Edited => EditedBrush,
        RowRenderState.Malformed => MalformedBrush,
        _ => Brushes.Transparent,
    };

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
