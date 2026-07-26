using System.Runtime.InteropServices;

namespace FileViewer.Core.Native;

/// <summary>
/// Offset/length pointer into the memory-mapped source file for a single logical row.
/// Stored in an <see cref="UnmanagedArray{T}"/>, never on the managed heap, so a
/// multi-million-row index never triggers Gen2/LOH collections.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public struct RowIndexEntry
{
    public long Offset;
    public int Length;

    public RowIndexEntry(long offset, int length)
    {
        Offset = offset;
        Length = length;
    }
}
