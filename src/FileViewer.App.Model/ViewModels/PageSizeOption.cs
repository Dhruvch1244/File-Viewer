namespace FileViewer.App.ViewModels;

/// <summary>How many rows the grid shows at once.</summary>
public enum PageSizeKind
{
    /// <summary>As many rows as fit the window, recomputed when it is resized.</summary>
    FitToWindow,

    /// <summary>A fixed number of rows per page.</summary>
    Fixed,

    /// <summary>Every row that passed the filters, on one page — scrolling rather than paging.</summary>
    AllRows,
}

/// <summary>
/// One entry of the "rows per page" picker. "All rows" is not the expensive option it sounds like:
/// the grid realizes only the rows on screen and this app only reads and decodes the rows it is
/// asked for, so the page size was never a limit on what could be shown — only on what was.
/// </summary>
public sealed record PageSizeOption(string Label, PageSizeKind Kind, int Rows = 0)
{
    public static readonly IReadOnlyList<PageSizeOption> All =
    [
        new("Fit to window", PageSizeKind.FitToWindow),
        new("50 rows", PageSizeKind.Fixed, 50),
        new("100 rows", PageSizeKind.Fixed, 100),
        new("250 rows", PageSizeKind.Fixed, 250),
        new("500 rows", PageSizeKind.Fixed, 500),
        new("1,000 rows", PageSizeKind.Fixed, 1_000),
        new("5,000 rows", PageSizeKind.Fixed, 5_000),
        new("All rows", PageSizeKind.AllRows),
    ];

    public static readonly PageSizeOption Default = All[0];

    /// <summary>The page size this option asks the row collection for — null meaning "no paging".</summary>
    public int? RowsPerPage => Kind switch
    {
        PageSizeKind.Fixed => Rows,
        PageSizeKind.AllRows => null,
        _ => null, // fit-to-window: the window measures and sets it
    };

    /// <summary>
    /// The form stored in settings so the choice survives a restart: the row count for a fixed size,
    /// 0 for fit-to-window, -1 for all rows.
    /// </summary>
    public int ToSetting() => Kind switch
    {
        PageSizeKind.Fixed => Rows,
        PageSizeKind.AllRows => -1,
        _ => 0,
    };

    public static PageSizeOption FromSetting(int? setting) => setting switch
    {
        null or 0 => Default,
        -1 => All[^1],
        // An unrecognized row count (an older build, a hand-edited settings file) falls back to the
        // nearest offered size rather than being dropped.
        int rows => All.Where(option => option.Kind == PageSizeKind.Fixed)
                       .OrderBy(option => Math.Abs(option.Rows - rows))
                       .FirstOrDefault() ?? Default,
    };

    public override string ToString() => Label;
}
