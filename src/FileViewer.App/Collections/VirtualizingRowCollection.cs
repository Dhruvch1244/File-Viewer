using System.Collections;
using System.Collections.Specialized;
using FileViewer.App.ViewModels;
using FileViewer.Core.Filtering;
using FileViewer.Core.Native;
using FileViewer.Core.Overlay;
using FileViewer.Core.Session;

namespace FileViewer.App.Collections;

/// <summary>
/// On-demand data source for the WPF <c>DataGrid</c>'s virtualizing panel: resolves
/// <c>visualIndex -&gt; base/synthetic row index -&gt; <see cref="RowViewModel"/></c> only when the
/// panel actually asks for that index, so rendering never materializes every row in the file (the
/// crux of keeping a 2 GB file's grid off the GC heap). Implements the plain, non-generic
/// <see cref="IList"/> — what <c>ItemsControl</c>/<c>DataGrid</c> actually probe for to support
/// virtualization without realizing the whole collection up front.
///
/// "Effective order" (which rows are visible, and in what order) is computed from the session's
/// current sort order plus any live Added/Duplicated rows, skipping Deleted rows entirely — a
/// deleted row disappears from the grid rather than rendering as a gap or a blank row. This is
/// cached and only rebuilt when <see cref="Invalidate"/> is called (after a structural edit:
/// add/delete/duplicate/undo/sort — never for a plain cell edit, which doesn't change row
/// membership or order).
/// </summary>
public sealed class VirtualizingRowCollection(FileViewerSession session) : IList, INotifyCollectionChanged
{
    private long[]? _effectiveOrder;
    private string? _filterText;

    public event NotifyCollectionChangedEventHandler? CollectionChanged;

    private long[] EffectiveOrder => _effectiveOrder ??= BuildEffectiveOrder();

    private long[] BuildEffectiveOrder()
    {
        var order = new List<long>();

        UnmanagedArray<SortKey> current = session.CurrentOrder;
        for (nuint i = 0; i < current.Count; i++)
        {
            long rowIndex = current[i].RowIndex;
            if (session.Overlay.GetRowState(rowIndex) != RowState.Deleted)
            {
                order.Add(rowIndex);
            }
        }

        foreach (RowOp op in session.Overlay.RowOps)
        {
            if ((op.Type is RowOpType.Add or RowOpType.Duplicate) && session.Overlay.GetRowState(op.RowIndex) != RowState.Deleted)
            {
                order.Add(op.RowIndex);
            }
        }

        if (!string.IsNullOrEmpty(_filterText))
        {
            order = RowFilter.Filter(order, _filterText, session.FileIndex, session.Overlay, session.Cache);
        }

        return [.. order];
    }

    /// <summary>Recomputes which rows are visible and in what order, and notifies the grid to re-query everything.</summary>
    public void Invalidate()
    {
        _effectiveOrder = null;
        CollectionChanged?.Invoke(this, new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset));
    }

    /// <summary>Sets (or clears, via null/empty) the arbitrary-column substring filter and invalidates the effective order.</summary>
    public void ApplyFilter(string? filterText)
    {
        _filterText = filterText;
        Invalidate();
    }

    public int Count => EffectiveOrder.Length;

    public object? this[int index]
    {
        get => new RowViewModel(session, EffectiveOrder[index]);
        set => throw new NotSupportedException();
    }

    public bool IsFixedSize => false;
    public bool IsReadOnly => true;
    public bool IsSynchronized => false;
    public object SyncRoot { get; } = new();

    public IEnumerator GetEnumerator()
    {
        long[] order = EffectiveOrder;
        for (int i = 0; i < order.Length; i++)
        {
            yield return new RowViewModel(session, order[i]);
        }
    }

    public bool Contains(object? value) => value is RowViewModel row && Array.IndexOf(EffectiveOrder, row.RowIndex) >= 0;

    public int IndexOf(object? value) => value is RowViewModel row ? Array.IndexOf(EffectiveOrder, row.RowIndex) : -1;

    public void CopyTo(Array array, int index)
    {
        long[] order = EffectiveOrder;
        for (int i = 0; i < order.Length; i++)
        {
            array.SetValue(new RowViewModel(session, order[i]), index + i);
        }
    }

    int IList.Add(object? value) => throw new NotSupportedException();
    void IList.Clear() => throw new NotSupportedException();
    void IList.Insert(int index, object? value) => throw new NotSupportedException();
    void IList.Remove(object? value) => throw new NotSupportedException();
    void IList.RemoveAt(int index) => throw new NotSupportedException();
}
