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

    /// <summary>
    /// Some real-world exports (Bloomberg Data License "getdata" jobs among them) spell the header
    /// marker "IMAHDR" instead of "INAHDR". Both are accepted as the file-opening marker line.
    /// </summary>
    public const string HeaderStartAlt = "IMAHDR";

    /// <summary>
    /// Optional marker line some exports emit immediately after the header marker, before any
    /// KEY=VALUE metadata. Recognized and skipped silently — it carries no data of its own.
    /// </summary>
    public const string FileStart = "START-OF-FILE";

    public const string FieldsStart = "START-OF-FIELDS";
    public const string FieldsEnd = "END-OF-FIELDS";
    public const string DataStart = "START-OF-DATA";
    public const string DataEnd = "END-OF-DATA";
    public const string Trailer = "INATRL";

    /// <summary>Mirrors <see cref="HeaderStartAlt"/>: some exports spell the closing trailer marker "IMATRL" instead of "INATRL". Both are accepted.</summary>
    public const string TrailerAlt = "IMATRL";

    /// <summary>Mirrors <see cref="FileStart"/> at the other end of the file — an optional marker line some exports emit right before the trailer marker. Recognized and skipped silently.</summary>
    public const string FileEnd = "END-OF-FILE";

    public const string DelimiterKey = "DELIMITER";
    public const string DataRecordsKey = "DATARECORDS";

    /// <summary>
    /// Synthetic column names unconditionally prepended to the declared field list when it doesn't
    /// already start with "_ID": every Bloomberg "getdata"-style DIF data row carries a security
    /// identifier, an error/return code, and the count of fields returned as its first three
    /// delimiter-separated values, ahead of the actually-requested field values — regardless of
    /// whether those three are declared in START-OF-FIELDS. This is a fixed property of the format,
    /// not something inferred per file. See <see cref="DifHeaderParser.ParseHeaderAndFields"/>.
    /// </summary>
    public static readonly string[] ImplicitRecordPrefixColumns = ["_ID", "_ERR", "_SIZE"];

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
