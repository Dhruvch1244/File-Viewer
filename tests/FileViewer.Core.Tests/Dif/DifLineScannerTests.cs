using System.Text;
using FileViewer.Core.Dif;

namespace FileViewer.Core.Tests.Dif;

public class DifLineScannerTests
{
    [Fact]
    public void TrimTrailingCr_RemovesTrailingCarriageReturn()
    {
        ReadOnlySpan<byte> line = Encoding.ASCII.GetBytes("hello\r");

        Assert.Equal("hello", Encoding.ASCII.GetString(DifLineScanner.TrimTrailingCr(line)));
    }

    [Fact]
    public void TrimTrailingCr_NoTrailingCr_ReturnsUnchanged()
    {
        ReadOnlySpan<byte> line = Encoding.ASCII.GetBytes("hello");

        Assert.Equal("hello", Encoding.ASCII.GetString(DifLineScanner.TrimTrailingCr(line)));
    }

    [Fact]
    public void FindNextNewLine_FindsFirstOccurrenceAtOrAfterStart()
    {
        byte[] data = Encoding.ASCII.GetBytes("abc\ndef\nghi");

        Assert.Equal(3, DifLineScanner.FindNextNewLine(data, 0));
        Assert.Equal(7, DifLineScanner.FindNextNewLine(data, 4));
        Assert.Equal(-1, DifLineScanner.FindNextNewLine(data, 8));
    }

    [Fact]
    public void LineEqualsMarker_MatchesExactlyAndIgnoresTrailingCr()
    {
        Assert.True(DifLineScanner.LineEqualsMarker(Encoding.ASCII.GetBytes("INAHDR"), "INAHDR"));
        Assert.True(DifLineScanner.LineEqualsMarker(Encoding.ASCII.GetBytes("INAHDR\r"), "INAHDR"));
        Assert.False(DifLineScanner.LineEqualsMarker(Encoding.ASCII.GetBytes("INAHDRX"), "INAHDR"));
        Assert.False(DifLineScanner.LineEqualsMarker(Encoding.ASCII.GetBytes("INAHD"), "INAHDR"));
    }

    [Fact]
    public void TryParseKeyValue_SplitsOnFirstEquals()
    {
        bool ok = DifLineScanner.TryParseKeyValue(
            Encoding.ASCII.GetBytes("DELIMITER=|"), DifFormatOptions.TextEncoding, out string key, out string value);

        Assert.True(ok);
        Assert.Equal("DELIMITER", key);
        Assert.Equal("|", value);
    }

    [Fact]
    public void TryParseKeyValue_NoEquals_ReturnsFalse()
    {
        bool ok = DifLineScanner.TryParseKeyValue(
            Encoding.ASCII.GetBytes("NOT-A-KEY-VALUE-LINE"), DifFormatOptions.TextEncoding, out _, out _);

        Assert.False(ok);
    }

    [Fact]
    public void TryReadLine_IteratesAllLinesIncludingUnterminatedFinalLine()
    {
        byte[] data = Encoding.ASCII.GetBytes("one\ntwo\nthree");
        int pos = 0;
        var lines = new List<string>();

        while (DifLineScanner.TryReadLine(data, ref pos, out ReadOnlySpan<byte> line))
        {
            lines.Add(Encoding.ASCII.GetString(line));
        }

        Assert.Equal(["one", "two", "three"], lines);
    }

    [Fact]
    public void TryReadLine_HandlesCrlf()
    {
        byte[] data = Encoding.ASCII.GetBytes("one\r\ntwo\r\n");
        int pos = 0;
        var lines = new List<string>();

        while (DifLineScanner.TryReadLine(data, ref pos, out ReadOnlySpan<byte> line))
        {
            lines.Add(Encoding.ASCII.GetString(DifLineScanner.TrimTrailingCr(line)));
        }

        Assert.Equal(["one", "two"], lines);
    }
}
