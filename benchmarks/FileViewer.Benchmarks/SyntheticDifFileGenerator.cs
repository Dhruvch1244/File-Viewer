using System.Text;

namespace FileViewer.Benchmarks;

/// <summary>Generates synthetic DIF files at approximately the target sizes named in PRS §11's benchmark matrix (10 MB / 500 MB / 2 GB). Not shipped in Core — purely a benchmark-harness utility.</summary>
internal static class SyntheticDifFileGenerator
{
    // ~42 bytes/row for the fixed row shape below; row counts are chosen to land close to each target size.
    public const int RowCount10Mb = 238_000;
    public const int RowCount500Mb = 11_900_000;
    public const int RowCount2Gb = 51_000_000;

    public static string Generate(int rowCount)
    {
        string path = Path.Combine(Path.GetTempPath(), $"fileviewer-bench-{rowCount}-{Guid.NewGuid():N}.dif");
        using var writer = new StreamWriter(path, append: false, Encoding.ASCII) { NewLine = "\n" };

        writer.WriteLine("INAHDR");
        writer.WriteLine("FIRMNAME=benchmark");
        writer.WriteLine("DELIMITER=|");
        writer.WriteLine("START-OF-FIELDS");
        writer.WriteLine("_ID");
        writer.WriteLine("_ERR");
        writer.WriteLine("PRICE");
        writer.WriteLine("VOLUME");
        writer.WriteLine("CURRENCY");
        writer.WriteLine("END-OF-FIELDS");
        writer.WriteLine("START-OF-DATA");
        for (int i = 0; i < rowCount; i++)
        {
            writer.WriteLine($"SEC{i:D9} HK Equity|0|{100 + i % 500}.{i % 100:D2}|{1000 + i}|USD");
        }
        writer.WriteLine("END-OF-DATA");
        writer.WriteLine("INATRL");
        writer.WriteLine($"DATARECORDS={rowCount}");

        return path;
    }
}
