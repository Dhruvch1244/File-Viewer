using System.Text;

namespace FileViewer.Core.Dif;

/// <summary>
/// Marker literals and conventions for the Bloomberg DIF/GETDATA format, confirmed against a real
/// sample export (FixedIncomeAsia.dif). The field delimiter is intentionally not a fixed constant
/// here — it is declared per-file via the <see cref="DelimiterKey"/> header line and read by
/// <see cref="DifHeaderParser"/>, never guessed.
/// </summary>
public static class DifFormatOptions
{
    public const string HeaderStart = "INAHDR";
    public const string FieldsStart = "START-OF-FIELDS";
    public const string FieldsEnd = "END-OF-FIELDS";
    public const string DataStart = "START-OF-DATA";
    public const string DataEnd = "END-OF-DATA";
    public const string Trailer = "INATRL";

    public const string DelimiterKey = "DELIMITER";
    public const string DataRecordsKey = "DATARECORDS";

    /// <summary>Used only if a file omits the DELIMITER header key, so the file still opens (PRS §8 Reliability).</summary>
    public const char DefaultDelimiter = '|';

    /// <summary>
    /// Bound on how far back from EOF the trailer/END-OF-DATA scan looks before giving up and
    /// treating the data section as running to EOF. Comfortably covers realistic header/trailer
    /// sizes without risking an O(file size) scan when those markers are missing or corrupted.
    /// </summary>
    public const int TrailerScanWindowBytes = 1024 * 1024;

    public static readonly Encoding TextEncoding = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);
}
