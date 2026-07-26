using System.ComponentModel;
using FileViewer.Core.Caching;
using FileViewer.Core.Overlay;
using FileViewer.Core.Session;

namespace FileViewer.App.ViewModels;

/// <summary>
/// Thin per-row binding wrapper resolved on demand — the <see cref="VirtualizingRowCollection"/>
/// only ever creates these for rows the grid's virtualizing panel actually asks for. Exposes an
/// indexer (bound in XAML as <c>[0]</c>, <c>[1]</c>, ...) so a fixed, compile-time-unknown set of
/// DIF columns can still be edited in place through ordinary two-way `DataGridTextColumn` bindings.
/// </summary>
public sealed class RowViewModel(FileViewerSession session, long rowIndex) : INotifyPropertyChanged
{
    private static readonly ResolvedRow EmptyRow = new(0, [], RowRenderState.Normal);

    private ResolvedRow? _resolved;

    public event PropertyChangedEventHandler? PropertyChanged;

    public long RowIndex => rowIndex;

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
}
