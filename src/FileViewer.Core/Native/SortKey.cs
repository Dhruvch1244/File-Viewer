using System.Runtime.InteropServices;

namespace FileViewer.Core.Native;

/// <summary>
/// Lightweight sort key extracted once per row during indexing (the "_ID" field), stored
/// inline so sorting never re-touches the mapped file. <see cref="RowIndex"/> is a back-reference
/// to the row's stable position in the <see cref="RowIndexEntry"/> array; sorting permutes only
/// the <see cref="SortKey"/> array, so base row indices (and therefore edit-overlay keys) never
/// move.
/// </summary>
[StructLayout(LayoutKind.Sequential)]
public unsafe struct SortKey : IComparable<SortKey>
{
    /// <summary>
    /// Bound on the number of key bytes stored inline. Bloomberg "_ID" values observed so far are
    /// well under this; if a real dataset exceeds it, this is the single constant to change.
    /// </summary>
    public const int InlineKeyCapacity = 32;

    public long RowIndex;
    public int KeyLength;
    public fixed byte InlineKey[InlineKeyCapacity];

    /// <summary>Copies up to <see cref="InlineKeyCapacity"/> bytes of <paramref name="source"/> into the inline buffer.</summary>
    public void SetKey(ReadOnlySpan<byte> source)
    {
        int length = Math.Min(source.Length, InlineKeyCapacity);
        KeyLength = length;
        fixed (byte* dest = InlineKey)
        {
            source[..length].CopyTo(new Span<byte>(dest, length));
        }
    }

    public ReadOnlySpan<byte> GetKeySpan()
    {
        fixed (byte* p = InlineKey)
        {
            return new ReadOnlySpan<byte>(p, KeyLength);
        }
    }

    /// <summary>Byte-lexicographic comparison. Numeric "_ID" ordering, if ever required, needs a separate numeric comparer.</summary>
    public int CompareTo(SortKey other) => GetKeySpan().SequenceCompareTo(other.GetKeySpan());
}
