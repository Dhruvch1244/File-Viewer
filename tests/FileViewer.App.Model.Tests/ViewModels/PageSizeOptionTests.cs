using FileViewer.App.ViewModels;

namespace FileViewer.App.Model.Tests.ViewModels;

public class PageSizeOptionTests
{
    [Fact]
    public void FitToWindow_LeavesThePageSizeToTheWindow()
    {
        PageSizeOption option = PageSizeOption.Default;

        Assert.Equal(PageSizeKind.FitToWindow, option.Kind);
        Assert.Null(option.RowsPerPage);
    }

    [Fact]
    public void AllRows_MeansNoPaging()
    {
        PageSizeOption option = PageSizeOption.All[^1];

        Assert.Equal(PageSizeKind.AllRows, option.Kind);
        Assert.Null(option.RowsPerPage);
        Assert.Equal(-1, option.ToSetting());
    }

    [Fact]
    public void FixedSizes_RoundTripThroughSettings()
    {
        foreach (PageSizeOption option in PageSizeOption.All)
        {
            Assert.Equal(option, PageSizeOption.FromSetting(option.ToSetting()));
        }
    }

    [Fact]
    public void FromSetting_WithNothingStored_FallsBackToFitToWindow()
    {
        Assert.Equal(PageSizeOption.Default, PageSizeOption.FromSetting(null));
        Assert.Equal(PageSizeOption.Default, PageSizeOption.FromSetting(0));
    }

    [Fact]
    public void FromSetting_WithAnUnofferedRowCount_PicksTheNearestOfferedSize()
    {
        // An older build, or a hand-edited settings file, shouldn't silently reset the preference.
        PageSizeOption option = PageSizeOption.FromSetting(400);

        Assert.Equal(PageSizeKind.Fixed, option.Kind);
        Assert.Equal(500, option.Rows);
    }
}
