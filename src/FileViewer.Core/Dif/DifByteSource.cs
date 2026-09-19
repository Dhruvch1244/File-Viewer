using Microsoft.Win32.SafeHandles;
using FileViewer.Core.Indexing;

namespace FileViewer.Core.Dif;

/// <summary>
/// Random-access byte reader the structural scanners work against, so the same scanning code runs
/// over a real (potentially multi-gigabyte) file handle and over an in-memory buffer (fixtures,
/// tests) without either one having to be materialized the other's way. Deliberately read-only and
/// offset-addressed: nothing here ever holds more than the caller's own buffer.
/// </summary>
public interface IDifByteSource
{
    long Length { get; }

    /// <summary>Reads up to <paramref name="destination"/>.Length bytes starting at <paramref name="offset"/>, returning how many were actually read (0 at or past the end).</summary>
    int Read(Span<byte> destination, long offset);
}

public static class DifByteSource
{
    public static IDifByteSource FromArray(ReadOnlyMemory<byte> content) => new ArrayByteSource(content);

    public static IDifByteSource FromHandle(SafeFileHandle handle, long length) => new FileHandleByteSource(handle, length);

    private sealed class ArrayByteSource(ReadOnlyMemory<byte> content) : IDifByteSource
    {
        public long Length => content.Length;

        public int Read(Span<byte> destination, long offset)
        {
            if (offset >= content.Length || offset < 0) return 0;
            int available = (int)Math.Min(destination.Length, content.Length - offset);
            content.Span.Slice((int)offset, available).CopyTo(destination);
            return available;
        }
    }

    private sealed class FileHandleByteSource(SafeFileHandle handle, long length) : IDifByteSource
    {
        public long Length => length;

        public int Read(Span<byte> destination, long offset)
        {
            if (offset >= length || offset < 0) return 0;
            int toRead = (int)Math.Min(destination.Length, length - offset);
            return RandomAccessReader.ReadExactly(handle, destination[..toRead], offset);
        }
    }
}
