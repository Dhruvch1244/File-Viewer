using FileViewer.Core.Export;

namespace FileViewer.Core.Tests.Export;

public class ExportFileNamingTests
{
    [Theory]
    [InlineData("fixedincome_ext_asia.out.20260719", "fixedincome_ext_asia")]
    [InlineData("nonShareFuturesAsia.dif.20260424", "nonShareFuturesAsia")]
    [InlineData("myexport.csv.20260101", "myexport")]
    [InlineData("plainname", "plainname")]
    [InlineData("plainname.dif", "plainname")]
    [InlineData("plainname.20260719", "plainname")]
    public void ExtractBaseName_StripsDateAndFormatTokenSuffixes(string fileName, string expected)
    {
        Assert.Equal(expected, ExportFileNaming.ExtractBaseName(fileName));
    }

    [Fact]
    public void ExtractBaseName_IgnoresDirectoryComponent()
    {
        Assert.Equal("fixedincome_ext_asia", ExportFileNaming.ExtractBaseName(@"C:\data\fixedincome_ext_asia.out.20260719"));
    }

    [Fact]
    public void BuildFileName_ProducesBaseNameDotFormatDotDate()
    {
        string result = ExportFileNaming.BuildFileName("fixedincome_ext_asia.out.20260719", "dif", new DateOnly(2026, 7, 27));

        Assert.Equal("fixedincome_ext_asia.dif.20260727", result);
    }

    [Fact]
    public void BuildFileName_DoesNotStackASecondSuffixWhenReExportingAnAlreadyNamedFile()
    {
        // Re-exporting a file that was already opened in Bloomberg naming form shouldn't produce
        // "fixedincome_ext_asia.out.20260719.csv.20260727" — the old format/date must be replaced.
        string result = ExportFileNaming.BuildFileName("fixedincome_ext_asia.out.20260719", "csv", new DateOnly(2026, 7, 27));

        Assert.Equal("fixedincome_ext_asia.csv.20260727", result);
    }

    [Fact]
    public void BuildFileName_WorksFromAPlainSourceNameWithNoExistingSuffix()
    {
        string result = ExportFileNaming.BuildFileName("myfile.txt", "json", new DateOnly(2026, 1, 5));

        Assert.Equal("myfile.json.20260105", result);
    }
}
