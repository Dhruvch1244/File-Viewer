using System.Text;

namespace FileViewer.Core.Dif;

/// <summary>
/// Locates every data section of a DIF file — the multi-section ("bulk") counterpart to
/// <see cref="DifHeaderParser"/>'s bounded head/tail parse.
///
/// A bulk export repeats the whole block
/// <c>DATA=&lt;name&gt; / START-OF-FIELDS … END-OF-FIELDS / START-OF-DATA … END-OF-DATA</c> once per
/// requested bulk field, each block declaring its own field list. There is no way to know where
/// the second block starts without walking past the first block's data, so — unlike the
/// single-section parse, which only ever touches a head and a tail window — this is inherently one
/// forward pass over the file. Two things keep that affordable:
///
/// <list type="bullet">
/// <item>The pass never decodes or splits data rows. It jumps from one section to the next with a
/// vectorized <see cref="ReadOnlySpan{T}.IndexOf(ReadOnlySpan{T})"/> search for the END-OF-DATA
/// marker over <see cref="MarkerSearchWindowBytes"/>-sized windows, so the cost is a memory scan,
/// not a parse.</item>
/// <item>It is only run for files that look like bulk files at all (see
/// <see cref="DifBulkDetection"/>); everything else keeps the cheap bounded head/tail path.</item>
/// </list>
///
/// Row indexing is <em>not</em> part of this — see <see cref="Indexing.FileIndexer"/>, which
/// indexes one section at a time so the sections a user never opens are never paid for.
/// </summary>
public static class DifSectionScanner
{
    /// <summary>Size of the sliding window used to read header/field/trailer lines. Generous: these regions are small, and a window this size means a section's whole preamble is normally read in one go.</summary>
    public const int LineWindowBytes = 1024 * 1024;

    /// <summary>Size of the windows the END-OF-DATA marker search reads. Large enough that the per-read overhead is negligible against the scan itself, bounded so a 2 GB file never needs 2 GB of buffer.</summary>
    public const int MarkerSearchWindowBytes = 8 * 1024 * 1024;

    /// <summary>Upper bound on how many sections are collected before the scan gives up, so a pathological/corrupt file can't produce an unbounded section list.</summary>
    public const int MaxSections = 4096;

    public static string SynthesizeSectionName(int sectionIndex) => $"Section {sectionIndex + 1}";

    public static DifFileLayout Scan(ReadOnlyMemory<byte> content) => Scan(DifByteSource.FromArray(content));

    /// <summary>
    /// Walks <paramref name="source"/> from byte 0, returning every section it declares. Falls back
    /// to as much as it could make sense of rather than throwing: a file that runs out mid-section
    /// still yields the sections found so far, with a diagnostic explaining what was missing (PRS
    /// §8 Reliability).
    /// </summary>
    public static DifFileLayout Scan(IDifByteSource source, CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<DifDiagnostic>();
        var cursor = new DifLineCursor(source);

        if (!cursor.TryReadLine(out string firstLine, out _))
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"File does not start with '{DifFormatOptions.HeaderStart}' or '{DifFormatOptions.HeaderStartAlt}'.", 0));
            return DifFileLayout.Invalid(diagnostics);
        }

        string headerMarker;
        if (LineIsMarker(firstLine, DifFormatOptions.HeaderStart)) headerMarker = DifFormatOptions.HeaderStart;
        else if (LineIsMarker(firstLine, DifFormatOptions.HeaderStartAlt)) headerMarker = DifFormatOptions.HeaderStartAlt;
        else
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"File does not start with '{DifFormatOptions.HeaderStart}' or '{DifFormatOptions.HeaderStartAlt}'.", 0));
            return DifFileLayout.Invalid(diagnostics);
        }

        bool hasFileStartMarker = false;
        bool hasFileEndMarker = false;
        bool trailerSeen = false;
        string trailerMarker = DifFormatOptions.Trailer;
        var headerMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var trailerMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
        var sections = new List<DifSection>();

        // Metadata read since the last END-OF-DATA (or since the file header): it belongs to
        // whichever section's START-OF-FIELDS comes next, or to the trailer if none does.
        var pendingMetadata = new Dictionary<string, string>(StringComparer.Ordinal);

        // Where the next section's own block begins. Unset (-1) until either a DATA= line or the
        // START-OF-FIELDS line of the first section is seen, so that everything before it counts as
        // the file-level preamble a single-section DIF export reproduces verbatim.
        long pendingBlockStart = -1;
        long trailerStartOffset = source.Length;

        while (cursor.TryReadLine(out string line, out long lineStart))
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (LineIsMarker(line, DifFormatOptions.FieldsStart))
            {
                if (sections.Count >= MaxSections)
                {
                    diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                        $"Stopped after {MaxSections} sections; the rest of the file was not scanned.", lineStart));
                    break;
                }

                // A section can also name itself on the marker line ("START-OF-FIELDS|DATA=X"),
                // not only via a preceding/following DATA= line.
                if (TryExtractTrailingDataAttribute(line, DifFormatOptions.FieldsStart, out string inlineName))
                {
                    pendingMetadata[DifFormatOptions.SectionDataKey] = inlineName;
                }

                DifSection? section = ReadSection(
                    source, cursor, sections.Count, pendingBlockStart >= 0 ? pendingBlockStart : lineStart,
                    pendingMetadata, diagnostics, cancellationToken);
                if (section is null)
                {
                    break; // ran out of file mid-section; whatever was already found still stands
                }

                sections.Add(section);
                trailerStartOffset = section.DataEndLineEndOffset;
                pendingMetadata = new Dictionary<string, string>(StringComparer.Ordinal);
                pendingBlockStart = -1;
                continue;
            }

            if (LineIsMarker(line, DifFormatOptions.FileStart))
            {
                hasFileStartMarker = true;
                continue;
            }
            if (LineIsMarker(line, DifFormatOptions.FileEnd))
            {
                hasFileEndMarker = true;
                trailerSeen = true;
                continue;
            }
            if (LineIsMarker(line, DifFormatOptions.Trailer) || LineIsMarker(line, DifFormatOptions.TrailerAlt))
            {
                trailerMarker = LineIsMarker(line, DifFormatOptions.Trailer) ? DifFormatOptions.Trailer : DifFormatOptions.TrailerAlt;
                trailerSeen = true;
                continue;
            }

            if (!TryParseKeyValue(line, out string key, out string value))
            {
                if (line.Length > 0)
                {
                    diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                        "Ignoring malformed line (expected KEY=VALUE or a section marker).", lineStart));
                }
                continue;
            }

            if (sections.Count == 0)
            {
                // File-level preamble metadata — kept out of the first section's own metadata, with
                // the one exception of the DATA= attribute, which names that section rather than the
                // file (and marks where the file preamble ends).
                headerMetadata[key] = value;
                if (key == DifFormatOptions.SectionDataKey)
                {
                    pendingMetadata[key] = value;
                    if (pendingBlockStart < 0) pendingBlockStart = lineStart;
                }
                continue;
            }

            // A DATARECORDS line right after a section's END-OF-DATA is that section's own record
            // count, not the next section's metadata.
            if (key == DifFormatOptions.DataRecordsKey && !trailerSeen
                && sections[^1].DeclaredDataRecords is null && int.TryParse(value, out int sectionRecords))
            {
                sections[^1].DeclaredDataRecords = sectionRecords;
                trailerMetadata[key] = value;
                continue;
            }

            if (trailerSeen)
            {
                trailerMetadata[key] = value;
            }
            else
            {
                pendingMetadata[key] = value;
                // A DATA= line is the first line that unambiguously belongs to the *next* section,
                // so it (not the previous section's trailing DATARECORDS) is where that section's
                // reproducible block starts.
                if (key == DifFormatOptions.SectionDataKey && pendingBlockStart < 0) pendingBlockStart = lineStart;
            }
        }

        // Nothing declared a trailer marker, so whatever metadata was still pending at EOF is the
        // trailer's (matching the single-section parser, which treats bare KEY=VALUE lines after
        // END-OF-DATA as trailer metadata).
        if (sections.Count > 0)
        {
            foreach ((string key, string value) in pendingMetadata)
            {
                trailerMetadata.TryAdd(key, value);
            }
        }

        // A single-section file's trailer DATARECORDS describes that one section, so attribute it —
        // this is what the classic head/tail parse reports. A bulk file's file-level DATARECORDS is
        // the total across sections instead, so it is deliberately not attributed to any of them.
        if (sections.Count == 1 && sections[0].DeclaredDataRecords is null
            && trailerMetadata.TryGetValue(DifFormatOptions.DataRecordsKey, out string? totalRecords)
            && int.TryParse(totalRecords, out int parsedTotal))
        {
            sections[0].DeclaredDataRecords = parsedTotal;
        }

        if (sections.Count == 0)
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"No '{DifFormatOptions.FieldsStart}' section found in the file.", 0));
            return DifFileLayout.Invalid(diagnostics);
        }

        char delimiter = DifHeaderParser.ResolveDelimiter(headerMetadata, diagnostics);

        return new DifFileLayout
        {
            HeaderMarker = headerMarker,
            HasFileStartMarker = hasFileStartMarker,
            HeaderMetadata = headerMetadata,
            Delimiter = delimiter,
            Sections = sections,
            TrailerMetadata = trailerMetadata,
            TrailerMarker = trailerMarker,
            HasFileEndMarker = hasFileEndMarker,
            TrailerStartOffset = trailerStartOffset,
            IsValid = true,
            Diagnostics = diagnostics,
        };
    }

    /// <summary>
    /// Reads one section's field list and metadata (the cursor is positioned just after its
    /// START-OF-FIELDS line), then jumps past its data region to its END-OF-DATA, leaving the
    /// cursor on the line after it. Returns null if the file ends before the section is complete.
    /// </summary>
    private static DifSection? ReadSection(
        IDifByteSource source,
        DifLineCursor cursor,
        int sectionIndex,
        long blockStartOffset,
        Dictionary<string, string> metadata,
        List<DifDiagnostic> diagnostics,
        CancellationToken cancellationToken)
    {
        var columnNames = new List<string>();
        bool foundFieldsEnd = false;
        while (cursor.TryReadLine(out string line, out _))
        {
            if (LineIsMarker(line, DifFormatOptions.FieldsEnd))
            {
                foundFieldsEnd = true;
                break;
            }
            if (line.Length > 0)
            {
                columnNames.Add(line);
            }
        }
        if (!foundFieldsEnd)
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"Reached end of file before '{DifFormatOptions.FieldsEnd}'.", cursor.Position));
            return null;
        }

        bool foundDataStart = false;
        while (cursor.TryReadLine(out string line, out long lineStart))
        {
            if (LineIsMarker(line, DifFormatOptions.DataStart))
            {
                foundDataStart = true;
                break;
            }
            if (TryParseKeyValue(line, out string key, out string value))
            {
                metadata[key] = value;
            }
            else if (line.Length > 0)
            {
                diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                    $"Ignoring malformed line between '{DifFormatOptions.FieldsEnd}' and '{DifFormatOptions.DataStart}' (expected KEY=VALUE).", lineStart));
            }
        }
        if (!foundDataStart)
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Error,
                $"Reached end of file before '{DifFormatOptions.DataStart}'.", cursor.Position));
            return null;
        }

        DifHeaderParser.ApplyImplicitRecordPrefix(columnNames);

        long dataStart = cursor.Position;
        long dataEndExclusive;
        long dataEndLineEnd;
        if (TryFindMarkerLine(source, dataStart, DifFormatOptions.DataEnd, cancellationToken, out long markerStart, out long afterMarkerLine))
        {
            dataEndExclusive = markerStart;
            dataEndLineEnd = afterMarkerLine;
        }
        else
        {
            diagnostics.Add(new DifDiagnostic(DifDiagnosticSeverity.Warning,
                $"No '{DifFormatOptions.DataEnd}' marker found after '{DifFormatOptions.DataStart}'; treating this section's data as extending to end of file.", dataStart));
            dataEndExclusive = source.Length;
            dataEndLineEnd = source.Length;
        }
        cursor.Seek(dataEndLineEnd);

        metadata.TryGetValue(DifFormatOptions.SectionDataKey, out string? declaredName);
        bool hasDeclaredName = !string.IsNullOrWhiteSpace(declaredName);
        int? declaredRecords = metadata.TryGetValue(DifFormatOptions.DataRecordsKey, out string? recordsText)
            && int.TryParse(recordsText, out int parsedRecords) ? parsedRecords : null;

        return new DifSection
        {
            Index = sectionIndex,
            Name = hasDeclaredName ? declaredName!.Trim() : SynthesizeSectionName(sectionIndex),
            HasDeclaredName = hasDeclaredName,
            ColumnNames = columnNames,
            Metadata = metadata,
            BlockStartOffset = blockStartOffset,
            DataStartOffset = dataStart,
            DataEndOffsetExclusive = dataEndExclusive,
            DataEndLineEndOffset = dataEndLineEnd,
            DeclaredDataRecords = declaredRecords,
        };
    }

    /// <summary>
    /// Finds the first line at or after <paramref name="searchStart"/> (which must itself be a line
    /// start) that is <paramref name="marker"/>. Reads <see cref="MarkerSearchWindowBytes"/>-sized
    /// windows and locates candidates with a vectorized byte search rather than by splitting lines —
    /// this is what makes skipping a section's data region cost a memory scan instead of a parse.
    ///
    /// A candidate only counts if it begins a line and is followed by a non-word byte (or end of
    /// line/file) — the same boundary rule <see cref="DifLineScanner.LineEqualsMarker"/> applies, so
    /// a data row that merely happens to contain the marker text can't end a section early.
    /// </summary>
    public static bool TryFindMarkerLine(
        IDifByteSource source,
        long searchStart,
        string marker,
        CancellationToken cancellationToken,
        out long markerLineStart,
        out long nextLineStart)
    {
        markerLineStart = -1;
        nextLineStart = -1;

        byte[] markerBytes = Encoding.ASCII.GetBytes(marker);
        int overlap = markerBytes.Length + 1;
        byte[] buffer = new byte[MarkerSearchWindowBytes];
        long windowStart = searchStart;

        while (windowStart < source.Length)
        {
            cancellationToken.ThrowIfCancellationRequested();

            int read = source.Read(buffer, windowStart);
            if (read <= 0) break;

            var window = new ReadOnlySpan<byte>(buffer, 0, read);
            int searchFrom = 0;
            while (searchFrom < window.Length)
            {
                int hit = window[searchFrom..].IndexOf(markerBytes);
                if (hit < 0) break;

                int index = searchFrom + hit;
                long absolute = windowStart + index;
                bool atLineStart = absolute == searchStart
                    || (index > 0 ? window[index - 1] == (byte)'\n' : ByteAt(source, absolute - 1) == (byte)'\n');

                int afterIndex = index + markerBytes.Length;
                long afterAbsolute = absolute + markerBytes.Length;
                bool boundaryOk = afterAbsolute >= source.Length
                    || (afterIndex < window.Length ? !IsWordByte(window[afterIndex]) : !IsWordByte(ByteAt(source, afterAbsolute)));

                if (atLineStart && boundaryOk)
                {
                    markerLineStart = absolute;
                    var lineCursor = new DifLineCursor(source, absolute);
                    lineCursor.TryReadLine(out _, out _);
                    nextLineStart = lineCursor.Position;
                    return true;
                }

                searchFrom = index + 1;
            }

            if (windowStart + read >= source.Length) break;
            windowStart += read - overlap; // overlap so a marker straddling two windows is still found
        }

        return false;
    }

    private static byte ByteAt(IDifByteSource source, long offset)
    {
        if (offset < 0 || offset >= source.Length) return 0;
        Span<byte> one = stackalloc byte[1];
        return source.Read(one, offset) == 1 ? one[0] : (byte)0;
    }

    private static bool IsWordByte(byte b) =>
        (b >= (byte)'A' && b <= (byte)'Z')
        || (b >= (byte)'a' && b <= (byte)'z')
        || (b >= (byte)'0' && b <= (byte)'9')
        || b == (byte)'_';

    /// <summary>String counterpart of <see cref="DifLineScanner.LineEqualsMarker"/> — same "marker, optionally followed by more content, but not a longer word" rule.</summary>
    public static bool LineIsMarker(string line, string marker)
    {
        if (!line.StartsWith(marker, StringComparison.Ordinal)) return false;
        if (line.Length == marker.Length) return true;
        char next = line[marker.Length];
        return !(char.IsAsciiLetterOrDigit(next) || next == '_');
    }

    /// <summary>
    /// Pulls a <c>DATA=&lt;name&gt;</c> attribute out of the content packed onto a marker's own line
    /// (e.g. <c>START-OF-FIELDS|DATA=DVD_HIST</c>) — one of the three spellings a bulk export may
    /// use to name a section, alongside a DATA= line before or after the field list.
    /// </summary>
    public static bool TryExtractTrailingDataAttribute(string line, string marker, out string name)
    {
        name = string.Empty;
        if (line.Length <= marker.Length) return false;

        string trailing = line[marker.Length..];
        string token = DifFormatOptions.SectionDataKey + "=";
        int at = trailing.IndexOf(token, StringComparison.OrdinalIgnoreCase);
        if (at < 0) return false;

        int valueStart = at + token.Length;
        int valueEnd = valueStart;
        while (valueEnd < trailing.Length && trailing[valueEnd] is not ('|' or ',' or ';' or '\t' or ' ')) valueEnd++;

        name = trailing[valueStart..valueEnd].Trim();
        return name.Length > 0;
    }

    private static bool TryParseKeyValue(string line, out string key, out string value)
    {
        int eq = line.IndexOf('=');
        if (eq < 0)
        {
            key = string.Empty;
            value = string.Empty;
            return false;
        }
        key = line[..eq];
        value = line[(eq + 1)..];
        return true;
    }
}
