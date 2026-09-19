namespace FileViewer.Core.Dif;

/// <summary>
/// Decides whether a file should be opened through the multi-section
/// <see cref="DifSectionScanner"/> (one sequential pass over the file) or the cheap bounded
/// head/tail parse (<see cref="DifHeaderParser"/>, two small windows regardless of file size).
///
/// Two independent signals, either of which is enough:
/// <list type="bullet">
/// <item><b>The file name contains "bulk"</b> — the convention these exports are delivered under,
/// and the one a user can point at without opening the file.</item>
/// <item><b>The head of the file already shows bulk structure</b> — a <c>DATA=</c> attribute, or a
/// second START-OF-FIELDS block. This is what catches a bulk file that wasn't named like one.</item>
/// </list>
///
/// Both checks are cheap (a string check and a bounded head-window scan), which is the point: a
/// plain single-section file never pays for the sequential scan just to find out it didn't need it.
/// </summary>
public static class DifBulkDetection
{
    /// <summary>How much of the start of the file <see cref="ContentSuggestsBulk"/> inspects. Bulk structure that lies further in than this is still caught by the file-name check.</summary>
    public const int HeadProbeBytes = 512 * 1024;

    public static bool FileNameSuggestsBulk(string? path) =>
        !string.IsNullOrEmpty(path)
        && Path.GetFileName(path).Contains(DifFormatOptions.BulkFileNameMarker, StringComparison.OrdinalIgnoreCase);

    /// <summary>True if the first <see cref="HeadProbeBytes"/> bytes contain a <c>DATA=</c> attribute line or more than one START-OF-FIELDS block.</summary>
    public static bool ContentSuggestsBulk(ReadOnlySpan<byte> head)
    {
        int fieldsStartCount = 0;
        int pos = 0;
        while (DifLineScanner.TryReadLine(head, ref pos, out ReadOnlySpan<byte> line))
        {
            if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.FieldsStart))
            {
                fieldsStartCount++;
                if (fieldsStartCount > 1) return true;
                if (DifSectionScanner.TryExtractTrailingDataAttribute(
                        DifFormatOptions.TextEncoding.GetString(DifLineScanner.TrimTrailingCr(line)),
                        DifFormatOptions.FieldsStart, out _))
                {
                    return true;
                }
                continue;
            }

            if (DifLineScanner.TryParseKeyValue(line, DifFormatOptions.TextEncoding, out string key, out _)
                && string.Equals(key, DifFormatOptions.SectionDataKey, StringComparison.Ordinal))
            {
                return true;
            }
        }
        return false;
    }

    /// <summary>Convenience for callers that already hold the whole file in memory (fixtures, tests).</summary>
    public static bool ShouldScanSections(string? path, ReadOnlySpan<byte> head) =>
        FileNameSuggestsBulk(path) || ContentSuggestsBulk(head.Length <= HeadProbeBytes ? head : head[..HeadProbeBytes]);
}
