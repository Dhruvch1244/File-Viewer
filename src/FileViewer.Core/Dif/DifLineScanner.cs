using System.Text;

namespace FileViewer.Core.Dif;

/// <summary>
/// Allocation-free helpers for locating line boundaries and matching sentinel marker lines
/// directly against a byte span. Works identically whether the span is backed by a small
/// in-memory test fixture or a slice of a memory-mapped file, since <see cref="ReadOnlySpan{T}"/>
/// is agnostic to the backing store.
/// </summary>
public static class DifLineScanner
{
    public static ReadOnlySpan<byte> TrimTrailingCr(ReadOnlySpan<byte> line) =>
        line.Length > 0 && line[^1] == (byte)'\r' ? line[..^1] : line;

    /// <summary>Index of the next '\n' at or after <paramref name="start"/>, or -1 if none.</summary>
    public static int FindNextNewLine(ReadOnlySpan<byte> data, int start)
    {
        if (start >= data.Length) return -1;
        int idx = data[start..].IndexOf((byte)'\n');
        return idx < 0 ? -1 : start + idx;
    }

    /// <summary>
    /// True if <paramref name="line"/> is <paramref name="marker"/>, optionally followed by more
    /// content on the same line (e.g. some real-world exports pack extra pipe-delimited fields onto
    /// the marker line itself, such as "IMAHDR|BBGB0525-20260723-...|mfts-bwc|" rather than the
    /// marker standing alone). To avoid false-matching a coincidentally longer token that merely
    /// starts with the same letters (e.g. a column literally named "END-OF-FIELDS_X"), whatever
    /// immediately follows the marker must not itself be a "word" character (letter, digit, or
    /// underscore) — it has to be a real boundary (delimiter, punctuation, whitespace, or end of line).
    /// </summary>
    public static bool LineEqualsMarker(ReadOnlySpan<byte> line, string marker)
    {
        line = TrimTrailingCr(line);
        if (line.Length < marker.Length) return false;
        for (int i = 0; i < marker.Length; i++)
        {
            if (line[i] != (byte)marker[i]) return false;
        }
        if (line.Length == marker.Length) return true;

        return !IsWordByte(line[marker.Length]);
    }

    private static bool IsWordByte(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b == (byte)'_';

    /// <summary>Parses a "KEY=VALUE" line. Returns false if the line has no '=' separator.</summary>
    public static bool TryParseKeyValue(ReadOnlySpan<byte> line, Encoding encoding, out string key, out string value)
    {
        line = TrimTrailingCr(line);
        int eq = line.IndexOf((byte)'=');
        if (eq < 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }
        key = encoding.GetString(line[..eq]);
        value = encoding.GetString(line[(eq + 1)..]);
        return true;
    }

    /// <summary>
    /// Reads the line starting at <paramref name="pos"/> (up to but excluding the '\n', if any),
    /// advances <paramref name="pos"/> past it, and returns false once <paramref name="pos"/> is
    /// at or beyond the end of the content (including the final, terminator-less line at EOF).
    /// </summary>
    public static bool TryReadLine(ReadOnlySpan<byte> content, ref int pos, out ReadOnlySpan<byte> line)
    {
        if (pos >= content.Length)
        {
            line = default;
            return false;
        }
        int newLineIndex = FindNextNewLine(content, pos);
        if (newLineIndex < 0)
        {
            line = content[pos..];
            pos = content.Length;
            return true;
        }
        line = content[pos..newLineIndex];
        pos = newLineIndex + 1;
        return true;
    }
}
