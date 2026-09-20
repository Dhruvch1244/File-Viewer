using System.ComponentModel;
using FileViewer.Core.Caching;
using FileViewer.Core.Filtering;
using FileViewer.Core.Overlay;
using FileViewer.Core.Session;

namespace FileViewer.App.ViewModels;

/// <summary>
/// Thin per-row binding wrapper resolved on demand — the <see cref="VirtualizingRowCollection"/>
/// only ever creates these for rows the grid's virtualizing panel actually asks for. Exposes an
/// indexer (bound in XAML as <c>[0]</c>, <c>[1]</c>, ...) so a fixed, compile-time-unknown set of
/// DIF columns can still be edited in place through ordinary two-way `DataGridTextColumn` bindings.
/// </summary>
public sealed class RowViewModel(
    FileViewerSession session,
    long rowIndex,
    RowSelectionState selection,
    CompiledSearchQuery? highlight = null) : INotifyPropertyChanged
{
    private static readonly ResolvedRow EmptyRow = new(0, [], RowRenderState.Normal);

    private ResolvedRow? _resolved;
    private CellMatchView? _cellMatch;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long RowIndex => rowIndex;

    /// <summary>Whether this row is checked for bulk actions — backed by <see cref="RowSelectionState"/>, not this (short-lived, per-page) instance, so the check survives paging.</summary>
    public bool IsSelected
    {
        get => selection.IsSelected(rowIndex);
        set => selection.SetSelected(rowIndex, value);
    }

    private ResolvedRow Resolved => _resolved ??= session.Resolve(rowIndex) ?? EmptyRow;

    public RowRenderState RenderState => Resolved.RenderState;

    public string this[int columnIndex]
    {
        get
        {
            IReadOnlyList<string> fields = Resolved.FieldValues;
            return columnIndex >= 0 && columnIndex < fields.Count ? fields[columnIndex] : string.Empty;
        }
        set
        {
            IReadOnlyList<string> columnNames = session.FileIndex.Header.ColumnNames;
            if (columnIndex < 0 || columnIndex >= columnNames.Count) return;

            session.Overlay.EditCell(rowIndex, columnNames[columnIndex], value);
            if (rowIndex >= 0)
            {
                session.Cache.Invalidate(rowIndex);
            }
            _resolved = null;

            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(System.Windows.Data.Binding.IndexerName));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(RenderState)));
        }
    }

    /// <summary>
    /// Per-cell search-match flags, bound from XAML as <c>CellMatch[0]</c>, <c>CellMatch[1]</c>, …
    /// (each data column's cell style triggers its highlight off its own index). Exposed through a
    /// tiny view object because C# has no way to give a type a second, differently-named indexer
    /// alongside the value one.
    /// </summary>
    public CellMatchView CellMatch => _cellMatch ??= new CellMatchView(this);

    private bool IsCellMatch(int columnIndex)
    {
        if (highlight is null || highlight.IsEmpty) return false;

        IReadOnlyList<string> fields = Resolved.FieldValues;
        return columnIndex >= 0
            && columnIndex < fields.Count
            && highlight.HighlightsCell(columnIndex, fields[columnIndex]);
    }

    /// <summary>Indexer view over <see cref="RowViewModel"/>'s per-cell match flags — see <see cref="CellMatch"/>.</summary>
    public sealed class CellMatchView(RowViewModel row)
    {
        public bool this[int columnIndex] => row.IsCellMatch(columnIndex);
    }

    /// <summary>Every column name paired with this row's current value — backs the "View record" detail dialog.</summary>
    public IReadOnlyList<(string Column, string Value)> GetFieldPairs()
    {
        IReadOnlyList<string> columnNames = session.FileIndex.Header.ColumnNames;
        IReadOnlyList<string> values = Resolved.FieldValues;
        var pairs = new (string, string)[columnNames.Count];
        for (int i = 0; i < columnNames.Count; i++)
        {
            pairs[i] = (columnNames[i], i < values.Count ? values[i] : string.Empty);
        }
        return pairs;
    }
}
