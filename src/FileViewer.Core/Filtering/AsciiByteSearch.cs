namespace FileViewer.Core.Filtering;

/// <summary>
/// Case-insensitive substring search directly over UTF-8 bytes, for the common case of an ASCII
/// search term. Safe against UTF-8 content it wasn't given an ASCII needle for: every byte of a
/// multi-byte UTF-8 sequence has its high bit set, so an ASCII needle can never match part of one.
///
/// This exists so the main search box can answer "does this row contain X" without splitting the
/// row into fields or allocating a string per field — at a few million rows that decoding, not the
/// comparison, is the whole cost.
/// </summary>
public static class AsciiByteSearch
{
    /// <summary>
    /// True if <paramref name="haystack"/> contains <paramref name="needleUpper"/> ignoring ASCII
    /// case. <paramref name="needleUpper"/> must already be upper-cased ASCII (the caller does that
    /// once per query, not once per row).
    /// </summary>
    public static bool ContainsIgnoreCase(ReadOnlySpan<byte> haystack, ReadOnlySpan<byte> needleUpper)
    {
        if (needleUpper.Length == 0) return true;
        if (haystack.Length < needleUpper.Length) return false;

        byte firstUpper = needleUpper[0];
        byte firstLower = ToLower(firstUpper);

        int offset = 0;
        while (true)
        {
            ReadOnlySpan<byte> window = haystack[offset..];
            if (window.Length < needleUpper.Length) return false;

            // Vectorized hunt for a candidate first byte (either case), then a plain compare of the
            // rest — the same shape as a normal IndexOf, minus the case folding it can't do.
            int candidate = window[..(window.Length - needleUpper.Length + 1)].IndexOfAny(firstUpper, firstLower);
            if (candidate < 0) return false;

            int start = offset + candidate;
            int i = 1;
            for (; i < needleUpper.Length; i++)
            {
                if (ToUpper(haystack[start + i]) != needleUpper[i]) break;
            }
            if (i == needleUpper.Length) return true;

            offset = start + 1;
        }
    }

    private static byte ToUpper(byte b) => b >= (byte)'a' && b <= (byte)'z' ? (byte)(b - 32) : b;

    private static byte ToLower(byte b) => b >= (byte)'A' && b <= (byte)'Z' ? (byte)(b + 32) : b;
}
