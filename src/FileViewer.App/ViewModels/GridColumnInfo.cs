using FileViewer.App.Common;

namespace FileViewer.App.ViewModels;

/// <summary>Bindable show/hide state for one grid column — drives the column-chooser popup and the corresponding <c>DataGridColumn.Visibility</c> built in code-behind.</summary>
public sealed class GridColumnInfo(string name, int index) : ObservableObject
{
    private bool _isVisible = true;

    public string Name { get; } = name;
    public int Index { get; } = index;

    public bool IsVisible
    {
        get => _isVisible;
        set => SetField(ref _isVisible, value);
    }
}
