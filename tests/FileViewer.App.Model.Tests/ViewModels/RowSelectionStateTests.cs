using FileViewer.App.ViewModels;

namespace FileViewer.App.Model.Tests.ViewModels;

public class RowSelectionStateTests
{
    private static readonly long[] RowsInView = [10, 11, 12, 13, 14];

    [Fact]
    public void SetSelected_TracksIndividualRows()
    {
        var selection = new RowSelectionState();

        selection.SetSelected(11, true);
        selection.SetSelected(13, true);

        Assert.Equal(2, selection.Count);
        Assert.True(selection.IsSelected(11));
        Assert.False(selection.IsSelected(12));
        Assert.Equal([11L, 13L], selection.Resolve(RowsInView));
    }

    [Fact]
    public void SelectAllMatching_SelectsEveryRowWithoutBeingGivenThem()
    {
        var selection = new RowSelectionState();

        selection.SelectAllMatching(2_000_000);

        Assert.True(selection.IsSelectAllMode);
        Assert.Equal(2_000_000, selection.Count);
        // Any row matches, including ones this object has never been told about.
        Assert.True(selection.IsSelected(1));
        Assert.True(selection.IsSelected(1_999_999));
        Assert.Equal(RowsInView, selection.Resolve(RowsInView));
    }

    [Fact]
    public void UntickingInSelectAllMode_RemovesOnlyThatRow()
    {
        var selection = new RowSelectionState();
        selection.SelectAllMatching(2_000_000);

        selection.SetSelected(12, false);

        Assert.False(selection.IsSelected(12));
        Assert.True(selection.IsSelected(11));
        Assert.Equal(1_999_999, selection.Count);
        Assert.Equal([10L, 11L, 13L, 14L], selection.Resolve(RowsInView));
    }

    [Fact]
    public void ReTickingAnExcludedRow_PutsItBack()
    {
        var selection = new RowSelectionState();
        selection.SelectAllMatching(100);
        selection.SetSelected(12, false);

        selection.SetSelected(12, true);

        Assert.True(selection.IsSelected(12));
        Assert.Equal(100, selection.Count);
    }

    [Fact]
    public void UpdateMatchingRowCount_KeepsTheCountHonestWhenFiltersChange()
    {
        var selection = new RowSelectionState();
        selection.SelectAllMatching(2_000_000);

        selection.UpdateMatchingRowCount(40);

        Assert.Equal(40, selection.Count);
    }

    [Fact]
    public void UpdateMatchingRowCount_DoesNothingOutsideSelectAllMode()
    {
        var selection = new RowSelectionState();
        selection.SetSelected(11, true);

        selection.UpdateMatchingRowCount(2_000_000);

        Assert.Equal(1, selection.Count);
    }

    [Fact]
    public void Clear_LeavesSelectAllMode()
    {
        var selection = new RowSelectionState();
        selection.SelectAllMatching(2_000_000);

        selection.Clear();

        Assert.False(selection.IsSelectAllMode);
        Assert.Equal(0, selection.Count);
        Assert.False(selection.IsSelected(11));
        Assert.Empty(selection.Resolve(RowsInView));
    }

    [Fact]
    public void Resolve_OnlyEverReturnsRowsInView()
    {
        // "All matching" means matching the filters in force — not every row the file ever had.
        var selection = new RowSelectionState();
        selection.SelectAllMatching(2_000_000);

        Assert.Equal([10L, 12L], selection.Resolve([10, 12]));
    }

    [Fact]
    public void Changed_FiresOnlyWhenSomethingActuallyChanges()
    {
        var selection = new RowSelectionState();
        int changes = 0;
        selection.Changed += () => changes++;

        selection.SetSelected(11, true);
        selection.SetSelected(11, true);   // already selected
        selection.SetSelected(12, false);  // was not selected
        Assert.Equal(1, changes);

        selection.Clear();
        Assert.Equal(2, changes);

        selection.Clear();                 // already empty
        Assert.Equal(2, changes);
    }
}
