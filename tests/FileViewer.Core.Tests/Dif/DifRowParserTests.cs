using System.Text;
using FileViewer.Core.Dif;

namespace FileViewer.Core.Tests.Dif;

public class DifRowParserTests
{
    private static readonly byte[] SampleLine = Encoding.ASCII.GetBytes("SEC001 HK Equity|0|100.50\r");

    [Fact]
    public void CountFields_CountsDelimitedFieldsAndIgnoresTrailingCr()
    {
        Assert.Equal(3, DifRowParser.CountFields(SampleLine, (byte)'|'));
    }

    [Fact]
    public void ParseRow_MaterializesAllFieldsInOrder()
    {
        string[] fields = DifRowParser.ParseRow(SampleLine, (byte)'|', DifFormatOptions.TextEncoding);

        Assert.Equal(["SEC001 HK Equity", "0", "100.50"], fields);
    }

    [Fact]
    public void ParseRow_HandlesEmptyFields()
    {
        byte[] line = Encoding.ASCII.GetBytes("A||C");

        string[] fields = DifRowParser.ParseRow(line, (byte)'|', DifFormatOptions.TextEncoding);

        Assert.Equal(["A", "", "C"], fields);
    }

    [Fact]
    public void GetFieldSpan_ReturnsRequestedFieldWithoutMaterializingOthers()
    {
        ReadOnlySpan<byte> field = DifRowParser.GetFieldSpan(SampleLine, (byte)'|', 2);

        Assert.Equal("100.50", Encoding.ASCII.GetString(field));
    }

    [Fact]
    public void GetFieldSpan_IndexOutOfRange_ReturnsEmpty()
    {
        ReadOnlySpan<byte> field = DifRowParser.GetFieldSpan(SampleLine, (byte)'|', 10);

        Assert.True(field.IsEmpty);
    }

    [Fact]
    public void SplitFieldsInto_ProducesRangesMatchingParseRow()
    {
        ReadOnlySpan<byte> trimmed = DifLineScanner.TrimTrailingCr(SampleLine);
        int count = DifRowParser.CountFields(trimmed, (byte)'|');
        Span<Range> ranges = stackalloc Range[count];

        DifRowParser.SplitFieldsInto(trimmed, (byte)'|', ranges);

        Assert.Equal("SEC001 HK Equity", Encoding.ASCII.GetString(trimmed[ranges[0]]));
        Assert.Equal("0", Encoding.ASCII.GetString(trimmed[ranges[1]]));
        Assert.Equal("100.50", Encoding.ASCII.GetString(trimmed[ranges[2]]));
    }
}
