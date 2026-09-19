using FileViewer.Core.Filtering;

namespace FileViewer.App.ViewModels;

/// <summary>
/// A <see cref="SearchMatchMode"/> paired with the wording shown in the search box's mode dropdown.
/// Exists so the UI never renders raw enum names ("NotContains") at the user.
/// </summary>
public sealed record SearchModeOption(SearchMatchMode Mode, string Label)
{
    public static readonly IReadOnlyList<SearchModeOption> All =
    [
        new(SearchMatchMode.Contains, "contains"),
        new(SearchMatchMode.NotContains, "does not contain"),
        new(SearchMatchMode.Equals, "is"),
        new(SearchMatchMode.NotEquals, "is not"),
        new(SearchMatchMode.StartsWith, "starts with"),
        new(SearchMatchMode.EndsWith, "ends with"),
        new(SearchMatchMode.Regex, "matches regex"),
    ];

    public static readonly SearchModeOption Default = All[0];

    public override string ToString() => Label;
}
