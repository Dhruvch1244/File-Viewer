using System.Linq;
using System.Windows;

namespace FileViewer.App.Theme;

public enum AppTheme
{
    Light,
    Dark,
}

/// <summary>
/// Swaps which color-palette dictionary (<c>Colors.Light.xaml</c> / <c>Colors.Dark.xaml</c>) is
/// merged into <see cref="Application.Resources"/>. Every brush the app's styles use is referenced
/// via <c>DynamicResource</c> rather than <c>StaticResource</c> specifically so this swap re-themes
/// every open window immediately — DynamicResource re-resolves on the next resource-invalidation
/// pass, StaticResource would freeze at whatever was merged in at load time.
/// </summary>
public static class ThemeManager
{
    private static readonly Uri LightSource = new("Theme/Colors.Light.xaml", UriKind.Relative);
    private static readonly Uri DarkSource = new("Theme/Colors.Dark.xaml", UriKind.Relative);

    public static AppTheme Current { get; private set; } = AppTheme.Light;

    public static event Action<AppTheme>? ThemeChanged;

    public static void Toggle() => Apply(Current == AppTheme.Light ? AppTheme.Dark : AppTheme.Light);

    public static void Apply(AppTheme theme)
    {
        Current = theme;
        Uri source = theme == AppTheme.Dark ? DarkSource : LightSource;

        System.Collections.ObjectModel.Collection<ResourceDictionary> merged = Application.Current.Resources.MergedDictionaries;
        int existingIndex = merged.ToList().FindIndex(d =>
            d.Source is { } uri && (uri.OriginalString.EndsWith("Colors.Light.xaml") || uri.OriginalString.EndsWith("Colors.Dark.xaml")));

        var replacement = new ResourceDictionary { Source = source };
        if (existingIndex >= 0)
        {
            merged[existingIndex] = replacement;
        }
        else
        {
            merged.Insert(0, replacement);
        }

        ThemeChanged?.Invoke(theme);
    }
}
