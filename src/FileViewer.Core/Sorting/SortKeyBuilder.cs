using FileViewer.Core.Dif;
using FileViewer.Core.Native;

namespace FileViewer.Core.Sorting;

/// <summary>Extracts a row's sort key (the "_ID" field, by convention) during indexing.</summary>
public static class SortKeyBuilder
{
    public static SortKey Build(long rowIndex, ReadOnlySpan<byte> rowLine, byte delimiter, int sortColumnIndex)
    {
        var key = new SortKey { RowIndex = rowIndex };
        if (sortColumnIndex >= 0)
        {
            ReadOnlySpan<byte> fieldBytes = DifRowParser.GetFieldSpan(rowLine, delimiter, sortColumnIndex);
            key.SetKey(fieldBytes);
        }
        return key;
    }
}
