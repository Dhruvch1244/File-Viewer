using System.Text.RegularExpressions;

namespace FileViewer.Core.Export;

/// <summary>
/// Builds Bloomberg-style export file names — "{baseName}.{formatToken}.{yyyyMMdd}" — matching how
/// real Bloomberg Data License output is actually named (e.g. "fixedincome_ext_asia.out.20260719"),
/// not a plain "*.dif"/"*.csv" extension with no date. Also strips that same convention back off an
/// existing file name, so re-exporting a file that's already named this way doesn't stack a second
/// format token and date onto it.
/// </summary>
public static partial class ExportFileNaming
{
    /// <summary>
    /// Strips a trailing ".yyyymmdd" date suffix and, if present immediately before that, one
    /// trailing known format token — so "fixedincome_ext_asia.out.20260719" reduces to
    /// "fixedincome_ext_asia", the same as "nonShareFuturesAsia.dif.20260424" reduces to
    /// "nonShareFuturesAsia". A name with neither suffix is returned unchanged.
    /// </summary>
    public static string ExtractBaseName(string fileName)
    {
        // Deliberately not Path.GetFileName: a source path can be a Windows path (backslash
        // separators) even when this runs on a non-Windows test host, where Path's separator
        // handling is platform-specific and would leave the directory component attached.
        int lastSeparator = fileName.LastIndexOfAny(['/', '\\']);
        string name = lastSeparator >= 0 ? fileName[(lastSeparator + 1)..] : fileName;

        name = DateSuffixPattern().Replace(name, string.Empty);
        name = FormatTokenSuffixPattern().Replace(name, string.Empty);
        return name;
    }

    /// <summary>Builds "{baseName}.{formatToken}.{date:yyyyMMdd}" from an existing file name/path, a format token ("dif", "out", "csv", "tsv", "json"), and a date.</summary>
    public static string BuildFileName(string sourceFileName, string formatToken, DateOnly date) =>
        $"{ExtractBaseName(sourceFileName)}.{formatToken}.{date:yyyyMMdd}";

    [GeneratedRegex(@"\.\d{8}$")]
    private static partial Regex DateSuffixPattern();

    [GeneratedRegex(@"\.(dif|out|csv|tsv|json|txt)$", RegexOptions.IgnoreCase)]
    private static partial Regex FormatTokenSuffixPattern();
}
