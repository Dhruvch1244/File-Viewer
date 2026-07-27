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

        (string headerMarker, bool hasFileStartMarker, Dictionary<string, string> headerMetadata,
            List<string> columnNames, Dictionary<string, string> postFieldsMetadata, long dataStartOffset) = forwardResult.Value;

        char delimiter = ResolveDelimiter(headerMetadata, diagnostics);

        long tailWindowStart = ComputeTailWindowStart(dataStartOffset, content.Length);
        ReadOnlySpan<byte> tailWindow = content[checked((int)tailWindowStart)..];
        (Dictionary<string, string> trailerMetadata, long dataEndOffsetExclusive, string trailerMarker, bool hasFileEndMarker) =
            ParseTrailerAndDataEnd(tailWindow, tailWindowStart, content.Length, diagnostics);

        return BuildHeader(headerMarker, hasFileStartMarker, headerMetadata, delimiter, columnNames,
            postFieldsMetadata, trailerMetadata, trailerMarker, hasFileEndMarker, dataStartOffset, dataEndOffsetExclusive, diagnostics);
    }

    private static DifFileHeader BuildHeader(
        string headerMarker,
        bool hasFileStartMarker,
        Dictionary<string, string> headerMetadata,
        char delimiter,
        List<string> columnNames,
        Dictionary<string, string> postFieldsMetadata,
        Dictionary<string, string> trailerMetadata,
        string trailerMarker,
        bool hasFileEndMarker,
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
            HeaderMarker = headerMarker,
            HasFileStartMarker = hasFileStartMarker,
            HeaderMetadata = headerMetadata,
            Delimiter = delimiter,
            ColumnNames = columnNames,
            PostFieldsMetadata = postFieldsMetadata,
            TrailerMetadata = trailerMetadata,
            TrailerMarker = trailerMarker,
            HasFileEndMarker = hasFileEndMarker,
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
    /// INAHDR/IMAHDR, header KEY=VALUE lines, START-OF-FIELDS, one column name per line,
    /// END-OF-FIELDS, and finally START-OF-DATA. Returns null (with diagnostics explaining why) if
    /// any required marker is missing — the file cannot be treated as DIF at all. Everything
    /// returned here (which marker spelling, whether START-OF-FILE was present, and the pre-/post-
    /// fields metadata split) exists so a later DIF export can reproduce the source file's structure
    /// instead of silently normalizing or dropping it.
    /// </summary>
    public static (string HeaderMarker, bool HasFileStartMarker, Dictionary<string, string> HeaderMetadata,
        List<string> ColumnNames, Dictionary<string, string> PostFieldsMetadata, long DataStartOffset)?
        ParseHeaderAndFields(ReadOnlySpan<byte> headWindow, List<DifDiagnostic> diagnostics)
    {
        int pos = 0;

        if (!DifLineScanner.TryReadLine(headWindow, ref pos, out ReadOnlySpan<byte> firstLine))
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"File does not start with '{DifFormatOptions.HeaderStart}' or '{DifFormatOptions.HeaderStartAlt}'.", 0));
            return null;
        }
        string headerMarker;
        if (DifLineScanner.LineEqualsMarker(firstLine, DifFormatOptions.HeaderStart)) headerMarker = DifFormatOptions.HeaderStart;
        else if (DifLineScanner.LineEqualsMarker(firstLine, DifFormatOptions.HeaderStartAlt)) headerMarker = DifFormatOptions.HeaderStartAlt;
        else
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"File does not start with '{DifFormatOptions.HeaderStart}' or '{DifFormatOptions.HeaderStartAlt}'.", 0));
            return null;
        }

        bool hasFileStartMarker = false;
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
                hasFileStartMarker = true;
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
        // same way the header preamble does, instead of requiring exact adjacency. Kept in a
        // separate dictionary from the header preamble's so a DIF export can put them back exactly
        // where they came from (after END-OF-FIELDS, not folded into the header before START-OF-FIELDS).
        var postFieldsMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
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
                postFieldsMetadata[key] = value;
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

        ApplyImplicitRecordPrefix(columnNames);

        return (headerMarker, hasFileStartMarker, headerMetadata, columnNames, postFieldsMetadata, pos);
    }

    /// <summary>
    /// Bloomberg Data License "getdata" jobs (and similar) always emit a security identifier, an
    /// error/return code, and the count of fields actually returned as the first three
    /// delimiter-separated values of <b>every</b> data row — regardless of what was declared between
    /// START-OF-FIELDS and END-OF-FIELDS, since those three are protocol plumbing, not requested
    /// fields. This is a fixed structural property of the format, not something to infer from row
    /// contents (peeking at sample rows to guess whether it applies is fragile — short/optional
    /// trailing fields, files with only a handful of rows, etc. all make row-shape-based detection
    /// unreliable). So: whenever the declared column list doesn't already account for the prefix
    /// (its first column isn't the conventional "_ID"), unconditionally prepend
    /// <see cref="DifFormatOptions.ImplicitRecordPrefixColumns"/> so every column index lines up with
    /// its row's actual field index everywhere else in the app (grid, sort, export, edit) without any
    /// further special-casing.
    /// </summary>
    private static void ApplyImplicitRecordPrefix(List<string> columnNames)
    {
        if (columnNames.Count > 0 && columnNames[0] == DifFormatOptions.ImplicitRecordPrefixColumns[0])
        {
            return; // Already declared explicitly (or a file previously fixed up) — don't double-apply.
        }

        columnNames.InsertRange(0, DifFormatOptions.ImplicitRecordPrefixColumns);
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
    /// for the last END-OF-DATA line, then scans everything after it for trailer content. Real
    /// exports don't agree on the trailer's exact shape: some put an INATRL/IMATRL marker line
    /// immediately after END-OF-DATA with KEY=VALUE metadata (DATARECORDS, ...) after that; others
    /// put the KEY=VALUE metadata (DATARECORDS, TIMEFINISHED, ...) directly after END-OF-DATA with
    /// no marker at all, followed by an optional END-OF-FILE line and then the INATRL/IMATRL marker
    /// as the file's very last line (mirroring the header/START-OF-FILE pair at the top of the
    /// file). Rather than assume one fixed order, every line after END-OF-DATA is classified
    /// independently — a marker, or KEY=VALUE metadata, or (if neither) a malformed-line warning —
    /// so either arrangement, or a mix, is handled the same way. Falls back to treating the data
    /// section as extending to <paramref name="totalFileLength"/> if END-OF-DATA itself isn't found
    /// in the window — the file still opens (PRS §8 Reliability).
    /// </summary>
    public static (Dictionary<string, string> TrailerMetadata, long DataEndOffsetExclusive, string TrailerMarker, bool HasFileEndMarker)
        ParseTrailerAndDataEnd(ReadOnlySpan<byte> tailWindow, long tailWindowFileOffset, long totalFileLength, List<DifDiagnostic> diagnostics)
    {
        // Find the last END-OF-DATA line within the window ("last" defends against a data row that
        // happens to collide with the marker string).
        int? dataEndRelativeOffset = null;
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
            }
        }

        var trailerMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        string trailerMarker = DifFormatOptions.Trailer;
        bool hasFileEndMarker = false;
        bool foundTrailerMarker = false;

        if (dataEndRelativeOffset is int dataEndStart)
        {
            int pos = dataEndStart;
            DifLineScanner.TryReadLine(tailWindow, ref pos, out _); // consume the END-OF-DATA line itself
            while (DifLineScanner.TryReadLine(tailWindow, ref pos, out ReadOnlySpan<byte> line))
            {
                if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.Trailer))
                {
                    trailerMarker = DifFormatOptions.Trailer;
                    foundTrailerMarker = true;
                }
                else if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.TrailerAlt))
                {
                    trailerMarker = DifFormatOptions.TrailerAlt;
                    foundTrailerMarker = true;
                }
                else if (DifLineScanner.LineEqualsMarker(line, DifFormatOptions.FileEnd))
                {
                    hasFileEndMarker = true;
                }
                else if (DifLineScanner.TryParseKeyValue(line, DifFormatOptions.TextEncoding, out string key, out string value))
                {
                    trailerMetadata[key] = value;
                }
                else if (DifLineScanner.TrimTrailingCr(line).Length > 0)
                {
                    diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                        $"Ignoring malformed trailer line (expected KEY=VALUE, or '{DifFormatOptions.Trailer}'/'{DifFormatOptions.TrailerAlt}'/'{DifFormatOptions.FileEnd}').",
                        tailWindowFileOffset + pos));
                }
            }
        }

        if (!foundTrailerMarker)
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                $"No '{DifFormatOptions.Trailer}' or '{DifFormatOptions.TrailerAlt}' marker found within the trailing {DifFormatOptions.TrailerScanWindowBytes} bytes; treating trailer as whatever metadata (if any) was found.",
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

        return (trailerMetadata, dataEndOffsetExclusive, trailerMarker, hasFileEndMarker);
    }
}
