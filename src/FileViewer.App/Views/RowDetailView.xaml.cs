using System.ComponentModel;
using System.Linq;
using System.Windows;
using FileViewer.App.ViewModels;

namespace FileViewer.App.Views;

/// <summary>"View record" dialog: every column name paired with its current value for one row, editable in place — useful once a table has more columns than comfortably fit on screen.</summary>
public partial class RowDetailView : Window
{
    private readonly RowViewModel _row;

    public IReadOnlyList<FieldEditViewModel> Fields { get; }

    public RowDetailView(RowViewModel row)
    {
        _row = row;
        Fields = [.. row.GetFieldPairs().Select((p, i) => new FieldEditViewModel(i, p.Column, p.Value))];
        DataContext = this;
        InitializeComponent();
    }

    private void OnSaveClick(object sender, RoutedEventArgs e)
    {
        foreach (FieldEditViewModel field in Fields)
        {
            if (field.IsChanged)
            {
                _row[field.ColumnIndex] = field.Value;
            }
        }
        DialogResult = true;
        Close();
    }

    private void OnCancelClick(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }
}

/// <summary>One editable field in the record-detail dialog. Tracks its original value so Save only writes columns that actually changed.</summary>
public sealed class FieldEditViewModel : INotifyPropertyChanged
{
    private readonly string _originalValue;
    private string _value;

    public FieldEditViewModel(int columnIndex, string columnName, string originalValue)
    {
        ColumnIndex = columnIndex;
        ColumnName = columnName;
        _originalValue = originalValue;
        _value = originalValue;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public int ColumnIndex { get; }
    public string ColumnName { get; }

    public string Value
    {
        get => _value;
        set
        {
            if (_value == value) return;
            _value = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Value)));
        }
    }

    public bool IsChanged => _value != _originalValue;
}
