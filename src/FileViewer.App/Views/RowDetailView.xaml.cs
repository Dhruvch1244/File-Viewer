using System.Linq;
using System.Windows;
using FileViewer.App.ViewModels;

namespace FileViewer.App.Views;

/// <summary>Read-only "view record" dialog: every column name paired with its current value for one row — useful once a table has more columns than comfortably fit on screen.</summary>
public partial class RowDetailView : Window
{
    public IReadOnlyList<FieldPairViewModel> Fields { get; }

    public RowDetailView(RowViewModel row)
    {
        Fields = [.. row.GetFieldPairs().Select(p => new FieldPairViewModel(p.Column, p.Value))];
        DataContext = this;
        InitializeComponent();
    }

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();
}

/// <summary>Named wrapper for a (Column, Value) pair — WPF bindings need real reflectable properties, which a raw ValueTuple's named elements aren't at runtime.</summary>
public sealed record FieldPairViewModel(string Column, string Value);
