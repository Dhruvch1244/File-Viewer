using System.Text;
using FileViewer.Core.Native;

namespace FileViewer.Core.Tests.Native;

public class SortKeyTests
{
    private static SortKey MakeKey(string text, long rowIndex = 0)
    {
        var key = new SortKey { RowIndex = rowIndex };
        key.SetKey(Encoding.ASCII.GetBytes(text));
        return key;
    }

    [Fact]
    public void SetKey_ThenGetKeySpan_RoundTrips()
    {
        var key = MakeKey("SEC1000 HK Equity");

        string roundTripped = Encoding.ASCII.GetString(key.GetKeySpan());

        Assert.Equal("SEC1000 HK Equity", roundTripped);
    }

    [Fact]
    public void SetKey_LongerThanCapacity_TruncatesToInlineCapacity()
    {
        string longId = new string('A', SortKey.InlineKeyCapacity + 10);
        var key = MakeKey(longId);

        Assert.Equal(SortKey.InlineKeyCapacity, key.KeyLength);
        Assert.Equal(new string('A', SortKey.InlineKeyCapacity), Encoding.ASCII.GetString(key.GetKeySpan()));
    }

    [Fact]
    public void CompareTo_OrdersLexicographically()
    {
        var a = MakeKey("SEC1000");
        var b = MakeKey("SEC1001");

        Assert.True(a.CompareTo(b) < 0);
        Assert.True(b.CompareTo(a) > 0);
        Assert.Equal(0, a.CompareTo(MakeKey("SEC1000")));
    }

    [Fact]
    public void Span_Sort_OrdersByKeyAndPreservesRowIndexAsBackReference()
    {
        var keys = new SortKey[]
        {
            MakeKey("SEC1002", rowIndex: 2),
            MakeKey("SEC1000", rowIndex: 0),
            MakeKey("SEC1001", rowIndex: 1),
        };

        Array.Sort(keys);

        Assert.Equal(0L, keys[0].RowIndex);
        Assert.Equal(1L, keys[1].RowIndex);
        Assert.Equal(2L, keys[2].RowIndex);
    }
}
