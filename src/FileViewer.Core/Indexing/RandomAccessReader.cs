using Microsoft.Win32.SafeHandles;

namespace FileViewer.Core.Indexing;

/// <summary>
/// Thin helper around <see cref="RandomAccess"/>. A single <see cref="RandomAccess.Read(SafeFileHandle,Span{byte},long)"/>
/// call is allowed to return fewer bytes than requested (e.g. a network share, or a read that
/// crosses some internal I/O boundary) — <see cref="ReadExactly"/> loops until the destination is
/// fully filled or the file is exhausted, so every other reader in this project can treat a read as
/// atomic without re-deriving this loop itself.
/// </summary>
internal static class RandomAccessReader
{
    /// <summary>Returns the number of bytes actually read — less than <paramref name="destination"/>'s length only if the file ran out first.</summary>
    public static int ReadExactly(SafeFileHandle handle, Span<byte> destination, long fileOffset)
    {
        int totalRead = 0;
        while (totalRead < destination.Length)
        {
            int read = RandomAccess.Read(handle, destination[totalRead..], fileOffset + totalRead);
            if (read == 0) break; // EOF
            totalRead += read;
        }
        return totalRead;
    }
}
