using System.Text;

namespace FileViewer.Core.Dif;

/// <summary>
/// Mechanical, allocation-minimizing splitting/materializing of a single already-located data-row
/// line. Deliberately has no knowledge of the file's column count — mismatch detection between a
/// row's actual field count and the header's declared column count is the indexer's job (it has
/// both pieces of information and decides how to record a diagnostic), not this parser's.
/// </summary>
public static class DifRowParser
{
    /// <summary>Number of delimiter-separated fields in <paramref name="line"/> (CR-trimmed first).</summary>
    public static int CountFields(ReadOnlySpan<byte> line, byte delimiter)
    {
        line = DifLineScanner.TrimTrailingCr(line);
        int count = 1;
        foreach (byte b in line)
        {
            if (b == delimiter) count++;
        }
        return count;
    }

    /// <summary>
    /// Fills <paramref name="destination"/> with the [start,end) range of each field in
    /// <paramref name="line"/>. <paramref name="destination"/> must be exactly
    /// <see cref="CountFields"/> long.
    /// </summary>
    public static void SplitFieldsInto(ReadOnlySpan<byte> line, byte delimiter, Span<Range> destination)
    {
        line = DifLineScanner.TrimTrailingCr(line);
        int fieldIndex = 0;
        int start = 0;
        for (int i = 0; i <= line.Length; i++)
        {
            if (i == line.Length || line[i] == delimiter)
            {
                destination[fieldIndex] = new Range(start, i);
                fieldIndex++;
                start = i + 1;
            }
        }
    }

    /// <summary>Materializes every field of a data row as a string. Only call for rows actually being rendered/edited/exported.</summary>
    public static string[] ParseRow(ReadOnlySpan<byte> line, byte delimiter, Encoding encoding)
    {
        ReadOnlySpan<byte> trimmed = DifLineScanner.TrimTrailingCr(line);
        int fieldCount = CountFields(trimmed, delimiter);
        Span<Range> ranges = fieldCount <= 64 ? stackalloc Range[fieldCount] : new Range[fieldCount];
        SplitFieldsInto(trimmed, delimiter, ranges);

        var result = new string[fieldCount];
        for (int i = 0; i < fieldCount; i++)
        {
            result[i] = encoding.GetString(trimmed[ranges[i]]);
        }
        return result;
    }

    /// <summary>Extracts a single field's raw bytes without materializing the rest of the row (used for cheap sort-key extraction).</summary>
    public static ReadOnlySpan<byte> GetFieldSpan(ReadOnlySpan<byte> line, byte delimiter, int fieldIndex)
    {
        ReadOnlySpan<byte> trimmed = DifLineScanner.TrimTrailingCr(line);
        int start = 0;
        int currentField = 0;
        for (int i = 0; i <= trimmed.Length; i++)
        {
            if (i == trimmed.Length || trimmed[i] == delimiter)
            {
                if (currentField == fieldIndex)
                {
                    return trimmed[start..i];
                }
                currentField++;
                start = i + 1;
            }
        }
        return ReadOnlySpan<byte>.Empty;
    }
}
