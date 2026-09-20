namespace FileViewer.Core.Dif;

/// <summary>
/// Reads a DIF file's <em>structural</em> lines (header, field names, per-section metadata,
/// trailer) one at a time through a sliding window of at most
/// <see cref="DifSectionScanner.LineWindowBytes"/> bytes, so walking a file's structure never
/// requires a buffer sized to the file.
///
/// Deliberately materializes each line as a <see cref="string"/>: the lines it is pointed at are
/// small and few, and a string is what the callers compare and store anyway. Data rows are never
/// read through this — <see cref="DifSectionScanner.TryFindMarkerLine"/> jumps over them with a
/// vectorized byte search instead, and row indexing reads them in bulk (see
/// <see cref="Indexing.FileIndexer"/>).
/// </summary>
public sealed class DifLineCursor
{
    private readonly IDifByteSource _source;
    private readonly byte[] _buffer;
    private long _windowStart;
    private int _windowLength;
    private int _pos;

    public DifLineCursor(IDifByteSource source, long startOffset = 0, int windowBytes = DifSectionScanner.LineWindowBytes)
    {
        _source = source;
        _buffer = new byte[Math.Max(1024, windowBytes)];
        _windowStart = startOffset;
        _windowLength = 0;
        _pos = 0;
    }

    /// <summary>Absolute file offset of the next line to be read.</summary>
    public long Position => _windowStart + _pos;

    public void Seek(long offset)
    {
        _windowStart = offset;
        _windowLength = 0;
        _pos = 0;
    }

    /// <summary>
    /// Reads the next line (CR-trimmed, decoded as UTF-8) and advances past it. Returns false at
    /// end of file. A line longer than the window is returned truncated to the window rather than
    /// looping forever — no real DIF structural line comes close, and refusing to advance would be
    /// the worse failure mode.
    /// </summary>
    public bool TryReadLine(out string line, out long lineStart)
    {
        lineStart = Position;
        line = string.Empty;
        if (lineStart >= _source.Length) return false;

        int newLineIndex = IndexOfNewLine();
        if (newLineIndex < 0 && !WindowReachesEndOfSource)
        {
            Refill(lineStart);
            newLineIndex = IndexOfNewLine();
        }

        ReadOnlySpan<byte> raw = newLineIndex >= 0
            ? _buffer.AsSpan(_pos, newLineIndex - _pos)
            : _buffer.AsSpan(_pos, _windowLength - _pos);

        _pos = newLineIndex >= 0 ? newLineIndex + 1 : _windowLength;
        line = DifFormatOptions.TextEncoding.GetString(DifLineScanner.TrimTrailingCr(raw));
        return true;
    }

    private bool WindowReachesEndOfSource => _windowStart + _windowLength >= _source.Length;

    private int IndexOfNewLine()
    {
        if (_pos >= _windowLength) return -1;
        int found = _buffer.AsSpan(_pos, _windowLength - _pos).IndexOf((byte)'\n');
        return found < 0 ? -1 : _pos + found;
    }

    private void Refill(long offset)
    {
        _windowStart = offset;
        _pos = 0;
        _windowLength = Math.Max(0, _source.Read(_buffer, offset));
    }
}
