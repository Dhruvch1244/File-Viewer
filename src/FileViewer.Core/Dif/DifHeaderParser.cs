namespace FileViewer.Core.Dif;

/// <summary>
/// Locates the header/field/trailer markers in a DIF file without touching the (potentially huge)
/// data-row region in between: header and field names via a forward scan from the start of the
/// file (cheap — stops as soon as START-OF-DATA is found), trailer and END-OF-DATA via a scan
/// bounded to the last <see cref="DifFormatOptions.TrailerScanWindowBytes"/> bytes of the file.
/// Neither pass walks the bulk data rows; that is left to the indexer's parallel chunk scan.
///
/// The head/tail window steps are exposed separately (<see cref="ParseHeaderAndFields"/>,
/// <see cref="ComputeTailWindowStart"/>, <see cref="ParseTrailerAndDataEnd"/>) because a real
/// memory-mapped file can exceed <see cref="int.MaxValue"/> bytes (the limit of a single
/// <see cref="Span{T}"/>) even at the PRS's "up to 2 GB" target — the indexer constructs small,
/// bounded head/tail spans directly from the mapped pointer rather than one span over the whole
/// file. <see cref="Parse(ReadOnlySpan{byte})"/> is a convenience wrapper over those same steps
/// for small in-memory content (fixtures, tests, or any file that comfortably fits in one span).
/// </summary>
public static class DifHeaderParser
{
    /// <summary>
    /// Generous bound on how much of the start of the file the header + field-name list forward
    /// scan is allowed to span. Header metadata and column names are always small relative to file
    /// size; this only exists so Phase A never has to construct a head span sized to the whole file.
    /// </summary>
    public const int HeadWindowBytes = 4 * 1024 * 1024;

    public static DifFileHeader Parse(ReadOnlySpan<byte> content)
    {
        var diagnostics = new List<DifDiagnostic>();

        ReadOnlySpan<byte> headWindow = content.Length <= HeadWindowBytes ? content : content[..HeadWindowBytes];
        var forwardResult = ParseHeaderAndFields(headWindow, diagnostics);
        if (forwardResult is null)
        {
            return CreateInvalidHeader(diagnostics);
        }

        (Dictionary<string, string> headerMetadata, List<string> columnNames, long dataStartOffset) = forwardResult.Value;

        char delimiter = ResolveDelimiter(headerMetadata, diagnostics);

        long tailWindowStart = ComputeTailWindowStart(dataStartOffset, content.Length);
        ReadOnlySpan<byte> tailWindow = content[checked((int)tailWindowStart)..];
        (Dictionary<string, string> trailerMetadata, long dataEndOffsetExclusive) =
            ParseTrailerAndDataEnd(tailWindow, tailWindowStart, content.Length, diagnostics);

        return BuildHeader(headerMetadata, delimiter, columnNames, trailerMetadata, dataStartOffset, dataEndOffsetExclusive, diagnostics);
    }

    private static DifFileHeader BuildHeader(
        Dictionary<string, string> headerMetadata,
        char delimiter,
        List<string> columnNames,
        Dictionary<string, string> trailerMetadata,
        long dataStartOffset,
        long dataEndOffsetExclusive,
        List<DifDiagnostic> diagnostics)
    {
        int? declaredDataRecords = null;
        if (trailerMetadata.TryGetValue(DifFormatOptions.DataRecordsKey, out string? recordsText)
            && int.TryParse(recordsText, out int parsedRecords))
        {
            declaredDataRecords = parsedRecords;
        }

        return new DifFileHeader
        {
            HeaderMetadata = headerMetadata,
            Delimiter = delimiter,
            ColumnNames = columnNames,
            TrailerMetadata = trailerMetadata,
            DeclaredDataRecords = declaredDataRecords,
            DataStartOffset = dataStartOffset,
            DataEndOffsetExclusive = dataEndOffsetExclusive,
            IsValid = true,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Builds the placeholder <see cref="DifFileHeader"/> (IsValid = false) used when a required
    /// marker couldn't be located. Public so the indexer can produce the same shape of result when
    /// its own head-window parse fails, without duplicating the "empty" field values here.
    /// </summary>
    public static DifFileHeader CreateInvalidHeader(IReadOnlyList<DifDiagnostic> diagnostics) => new()
    {
        HeaderMetadata = new Dictionary<string, string>(),
        Delimiter = DifFormatOptions.DefaultDelimiter,
        ColumnNames = Array.Empty<string>(),
        TrailerMetadata = new Dictionary<string, string>(),
        DeclaredDataRecords = null,
        DataStartOffset = 0,
        DataEndOffsetExclusive = 0,
        IsValid = false,
        Diagnostics = diagnostics,
    };

    /// <summary>
    /// Forward-scans <paramref name="headWindow"/> (must start at absolute file offset 0) for
    /// INAHDR, header KEY=VALUE lines, START-OF-FIELDS, one column name per line, END-OF-FIELDS,
    /// and finally START-OF-DATA. Returns null (with diagnostics explaining why) if any required
    /// marker is missing — the file cannot be treated as DIF at all.
    /// </summary>
    public static (Dictionary<string, string> HeaderMetadata, List<string> ColumnNames, long DataStartOffset)?
        ParseHeaderAndFields(ReadOnlySpan<byte> headWindow, List<DifDiagnostic> diagnostics)
    {
        int pos = 0;

        if (!DifLineScanner.TryReadLine(headWindow, ref pos, out ReadOnlySpan<byte> firstLine)
            || !(DifLineScanner.LineEqualsMarker(firstLine, DifFormatOptions.HeaderStart)
                 || DifLineScanner.LineEqualsMarker(firstLine, DifFormatOptions.HeaderStartAlt)))
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"File does not start with '{DifFormatOptions.HeaderStart}' or '{DifFormatOptions.HeaderStartAlt}'.", 0));
            return null;
        }

        var headerMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        bool foundFieldsStart = false;
        while (DifLineScanner.TryReadLine(headWindow, ref pos, out ReadOnlySpan<byte> line))
        {
            if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.FieldsStart))
            {
                foundFieldsStart = true;
                break;
            }
            if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.FileStart))
            {
                continue;
            }
            if (DifLineScanner.TryParseKeyValue(line, DifFormatOptions.TextEncoding, out string key, out string value))
            {
                headerMetadata[key] = value;
            }
            else if (DifLineScanner.TrimTrailingCr(line).Length > 0)
            {
                diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                    "Ignoring malformed header metadata line (expected KEY=VALUE).", pos));
            }
        }
        if (!foundFieldsStart)
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"Reached end of head window before '{DifFormatOptions.FieldsStart}'.", pos));
            return null;
        }

        var columnNames = new List<string>();
        bool foundFieldsEnd = false;
        while (DifLineScanner.TryReadLine(headWindow, ref pos, out ReadOnlySpan<byte> line))
        {
            if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.FieldsEnd))
            {
                foundFieldsEnd = true;
                break;
            }
            ReadOnlySpan<byte> trimmed = DifLineScanner.TrimTrailingCr(line);
            if (trimmed.Length > 0)
            {
                columnNames.Add(DifFormatOptions.TextEncoding.GetString(trimmed));
            }
        }
        if (!foundFieldsEnd)
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"Reached end of head window before '{DifFormatOptions.FieldsEnd}'.", pos));
            return null;
        }
        if (columnNames.Count == 0)
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                "No column names found between START-OF-FIELDS and END-OF-FIELDS.", pos));
            return null;
        }

        // Some exports insert extra KEY=VALUE metadata (e.g. TIMESTARTED=...) between END-OF-FIELDS
        // and START-OF-DATA rather than having START-OF-DATA follow immediately. Skip over those the
        // same way the header preamble does, instead of requiring exact adjacency.
        bool foundDataStart = false;
        while (DifLineScanner.TryReadLine(headWindow, ref pos, out ReadOnlySpan<byte> line))
        {
            if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.DataStart))
            {
                foundDataStart = true;
                break;
            }
            if (DifLineScanner.TryParseKeyValue(line, DifFormatOptions.TextEncoding, out string key, out string value))
            {
                headerMetadata[key] = value;
            }
            else if (DifLineScanner.TrimTrailingCr(line).Length > 0)
            {
                diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                    $"Ignoring malformed line between '{DifFormatOptions.FieldsEnd}' and '{DifFormatOptions.DataStart}' (expected KEY=VALUE).", pos));
            }
        }
        if (!foundDataStart)
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"Expected '{DifFormatOptions.DataStart}' after '{DifFormatOptions.FieldsEnd}'.", pos));
            return null;
        }

        DetectAndApplyImplicitRecordPrefix(headWindow, pos, headerMetadata, columnNames);

        return (headerMetadata, columnNames, pos);
    }

    /// <summary>
    /// Bloomberg Data License "getdata" jobs (and similar) always emit a security identifier, an
    /// error/return code, and the count of fields actually returned as the first three
    /// delimiter-separated values of every data row — regardless of what was declared between
    /// START-OF-FIELDS and END-OF-FIELDS, since those three are protocol plumbing, not requested
    /// fields. When a file's declared column list doesn't already account for them (its first
    /// column isn't the conventional "_ID"), sample up to
    /// <see cref="DifFormatOptions.ImplicitRecordPrefixSampleRows"/> leading data rows: if the most
    /// common field-count-minus-declared-column-count across that sample is exactly three, prepend
    /// <see cref="DifFormatOptions.ImplicitRecordPrefixColumns"/> so every column index lines up with
    /// its row's actual field index everywhere else in the app (grid, sort, export, edit) without any
    /// further special-casing.
    /// </summary>
    private static void DetectAndApplyImplicitRecordPrefix(
        ReadOnlySpan<byte> headWindow, int dataStartPos, Dictionary<string, string> headerMetadata, List<string> columnNames)
    {
        if (columnNames.Count > 0 && columnNames[0] == DifFormatOptions.ImplicitRecordPrefixColumns[0])
        {
            return; // Already declared explicitly (or a file previously fixed up) — don't double-apply.
        }

        char delimiter = headerMetadata.TryGetValue(DifFormatOptions.DelimiterKey, out string? raw) && raw.Length == 1
            ? raw[0]
            : DifFormatOptions.DefaultDelimiter;

        var deltaFrequency = new Dictionary<int, int>();
        int pos = dataStartPos;
        int rowsSampled = 0;
        while (rowsSampled < DifFormatOptions.ImplicitRecordPrefixSampleRows
               && DifLineScanner.TryReadLine(headWindow, ref pos, out ReadOnlySpan<byte> line))
        {
            if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.DataEnd))
            {
                break; // Ran into the trailer within the head window — nothing past here is a data row.
            }

            ReadOnlySpan<byte> trimmed = DifLineScanner.TrimTrailingCr(line);
            if (trimmed.Length == 0)
            {
                continue; // Blank line (e.g. right before END-OF-DATA) — not a real row, don't count it.
            }

            int delta = DifRowParser.CountFields(trimmed, (byte)delimiter) - columnNames.Count;
            deltaFrequency[delta] = deltaFrequency.GetValueOrDefault(delta) + 1;
            rowsSampled++;
        }

        if (deltaFrequency.Count == 0)
        {
            return; // No data rows fell within the head window (or the file has no data) — can't sample.
        }

        int modeDelta = deltaFrequency.MaxBy(kv => kv.Value).Key;
        if (modeDelta == DifFormatOptions.ImplicitRecordPrefixColumns.Length)
        {
            columnNames.InsertRange(0, DifFormatOptions.ImplicitRecordPrefixColumns);
        }
    }

    public static char ResolveDelimiter(IReadOnlyDictionary<string, string> headerMetadata, List<DifDiagnostic> diagnostics)
    {
        if (headerMetadata.TryGetValue(DifFormatOptions.DelimiterKey, out string? raw) && raw.Length == 1)
        {
            return raw[0];
        }
        diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
            $"Missing or invalid '{DifFormatOptions.DelimiterKey}' header value; defaulting to '{DifFormatOptions.DefaultDelimiter}'."));
        return DifFormatOptions.DefaultDelimiter;
    }

    /// <summary>Absolute file offset where the bounded trailer/END-OF-DATA scan window should start.</summary>
    public static long ComputeTailWindowStart(long dataStartOffset, long totalFileLength) =>
        Math.Max(dataStartOffset, totalFileLength - DifFormatOptions.TrailerScanWindowBytes);

    /// <summary>
    /// Scans <paramref name="tailWindow"/> (the bytes of the file starting at absolute offset
    /// <paramref name="tailWindowFileOffset"/>, as computed by <see cref="ComputeTailWindowStart"/>)
    /// for the last END-OF-DATA and INATRL marker lines, and parses the trailer's KEY=VALUE lines.
    /// Falls back to treating the data section as extending to <paramref name="totalFileLength"/>
    /// and the trailer as empty if the respective markers aren't found in the window — the file
    /// still opens (PRS §8 Reliability).
    /// </summary>
    public static (Dictionary<string, string> TrailerMetadata, long DataEndOffsetExclusive) ParseTrailerAndDataEnd(
        ReadOnlySpan<byte> tailWindow, long tailWindowFileOffset, long totalFileLength, List<DifDiagnostic> diagnostics)
    {
        // Pass 1: find the last DataEnd / Trailer marker lines within the window (there should be
        // at most one of each; "last" defends against a data row that happens to collide with a
        // marker string). Offsets are tracked relative to the window, converted to absolute after.
        int? dataEndRelativeOffset = null;
        int? trailerRelativeOffset = null;
        {
            int pos = 0;
            while (pos < tailWindow.Length)
            {
                int lineStart = pos;
                if (!DifLineScanner.TryReadLine(tailWindow, ref pos, out ReadOnlySpan<byte> line)) break;
                if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.DataEnd))
                {
                    dataEndRelativeOffset = lineStart;
                }
                else if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.Trailer))
                {
                    trailerRelativeOffset = lineStart;
                }
            }
        }

        var trailerMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        if (trailerRelativeOffset is int trailerStart)
        {
            int pos = trailerStart;
            DifLineScanner.TryReadLine(tailWindow, ref pos, out _); // consume the INATRL marker line itself
            while (DifLineScanner.TryReadLine(tailWindow, ref pos, out ReadOnlySpan<byte> line))
            {
                if (DifLineScanner.TryParseKeyValue(line, DifFormatOptions.TextEncoding, out string key, out string value))
                {
                    trailerMetadata[key] = value;
                }
                else if (DifLineScanner.TrimTrailingCr(line).Length > 0)
                {
                    diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                        "Ignoring malformed trailer metadata line (expected KEY=VALUE).", tailWindowFileOffset + pos));
                }
            }
        }
        else
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                $"No '{DifFormatOptions.Trailer}' marker found within the trailing {DifFormatOptions.TrailerScanWindowBytes} bytes; treating trailer as empty.",
                totalFileLength));
        }

        long dataEndOffsetExclusive;
        if (dataEndRelativeOffset is int dataEnd)
        {
            dataEndOffsetExclusive = tailWindowFileOffset + dataEnd;
        }
        else
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                $"No '{DifFormatOptions.DataEnd}' marker found within the trailing {DifFormatOptions.TrailerScanWindowBytes} bytes; treating data section as extending to end of file.",
                totalFileLength));
            dataEndOffsetExclusive = totalFileLength;
        }

        return (trailerMetadata, dataEndOffsetExclusive);
    }
}
