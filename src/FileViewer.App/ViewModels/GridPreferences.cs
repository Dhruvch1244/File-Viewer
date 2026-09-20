using FileViewer.App.Common;

namespace FileViewer.App.ViewModels;

/// <summary>
/// Grid settings that belong to the person rather than to one file: picking "500 rows" once should
/// hold for the next file and the next run, not reset every time a tab opens. Shared by every
/// <see cref="GridViewModel"/> so changing it anywhere changes it everywhere, and watched by
/// <see cref="MainViewModel"/> so the choice is written to settings.
/// </summary>
public sealed class GridPreferences : ObservableObject
{
    private PageSizeOption _pageSize = PageSizeOption.Default;

    public PageSizeOption PageSize
    {
        get => _pageSize;
        set => SetField(ref _pageSize, value);
    }
}
