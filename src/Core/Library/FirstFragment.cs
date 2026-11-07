// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;

namespace SteamPoweredPlayer.Core.Library;

public enum FragmentStatus
{
    /// <summary>The first <c>moof</c>+<c>mdat</c> lies completely inside the bytes given.</summary>
    Found,
    /// <summary>The bytes end inside the structure; read up to <see cref="FirstFragmentInfo.RequiredBytes"/> from the file start and ask again.</summary>
    NeedMore,
    /// <summary>The head is all zero bytes (a crash leftover that keeps a normal chunk name).</summary>
    ZeroFilled,
    /// <summary>Not the layout we understand (or an absurd size). <see cref="FirstFragmentInfo.Error"/> says why.</summary>
    Invalid,
}

/// <summary>
/// Result of <see cref="FirstFragment.Find"/>. For <see cref="FragmentStatus.Found"/> the fragment (the IDR the trickplay decodes) is
/// <c>bytes[Offset .. Offset + Length)</c> of the chunk file and <see cref="RequiredBytes"/> = Offset + Length. For
/// <see cref="FragmentStatus.NeedMore"/>, <see cref="RequiredBytes"/> is a lower bound of what to read next; it is exact (the end of
/// the <c>mdat</c>) as soon as the <c>mdat</c> header was visible, so the adaptive read needs at most one extra read.
/// </summary>
public readonly record struct FirstFragmentInfo(FragmentStatus Status, long Offset, long Length, long RequiredBytes, string? Error = null);

/// <summary>
/// Pure walker over the head of a Steam chunk (<c>chunk-stream0-N.m4s</c>): finds the first <c>moof</c> and the <c>mdat</c> that follows
/// (spike S4: one sample per fragment, the first one is the IDR; <c>init-stream0.m4s</c> + that fragment is a valid fMP4 stream for ffmpeg).
/// The init segment is not needed: all sizes are in the box headers. Boxes before the <c>moof</c> (<c>styp</c>, <c>sidx</c>, <c>free</c>)
/// are skipped. This enables the adaptive read: a first read of <see cref="DefaultFirstRead"/> bytes, then at most one exact second read.
/// </summary>
public static class FirstFragment
{
    /// <summary>S4: 256 KiB covers the whole IDR in about half of the real chunks (mean 0.31 MB read per chunk).</summary>
    public const int DefaultFirstRead = 256 * 1024;
    /// <summary>The largest fragment accepted (S4 saw 906 KB; the format has no bound, so there is a checked cap).</summary>
    public const long MaxFragmentBytes = 32L * 1024 * 1024;
    private const long MaxSkippedBox = 1L << 20;

    public static FirstFragmentInfo Find(ReadOnlySpan<byte> head)
    {
        if (head.Length >= 8 && head[..8].IndexOfAnyExcept((byte)0) < 0) return Bad(FragmentStatus.ZeroFilled, "head is zero-filled");

        long pos = 0;
        while (true)
        {
            if (!ReadBox(head, pos, out var type, out var size, out var header, out var need))
                return need < 0 ? Bad(FragmentStatus.Invalid, $"bad box header at {pos}") : More(need);
            if (type == "moof")
            {
                if (size < 16 || size > MaxFragmentBytes) return Bad(FragmentStatus.Invalid, $"moof size {size} unsupported");
                var moofEnd = pos + size;
                if (moofEnd > head.Length) return More(moofEnd + 8);
                if (!HasTraf(head.Slice((int)(pos + header), (int)(size - header)))) return Bad(FragmentStatus.Invalid, "moof without traf");
                if (!ReadBox(head, moofEnd, out var next, out var mdatSize, out _, out need))
                    return need < 0 ? Bad(FragmentStatus.Invalid, "bad box after moof") : More(need);
                if (next != "mdat") return Bad(FragmentStatus.Invalid, $"box after moof is '{next}', not mdat");
                var end = moofEnd + mdatSize;
                if (end - pos > MaxFragmentBytes) return Bad(FragmentStatus.Invalid, $"first fragment of {end - pos} bytes exceeds the cap");
                return end > head.Length
                    ? new FirstFragmentInfo(FragmentStatus.NeedMore, pos, end - pos, end)
                    : new FirstFragmentInfo(FragmentStatus.Found, pos, end - pos, end);
            }
            if (type == "mdat") return Bad(FragmentStatus.Invalid, "mdat before any moof");
            if (size > MaxSkippedBox) return Bad(FragmentStatus.Invalid, $"box '{type}' of {size} bytes before the first moof");
            pos += size;
        }
    }

    /// <summary>Reads a box header at <paramref name="pos"/>. False with need ≥ 0: more bytes needed (up to <paramref name="need"/>); need &lt; 0: invalid.</summary>
    private static bool ReadBox(ReadOnlySpan<byte> s, long pos, out string type, out long size, out long header, out long need)
    {
        type = ""; size = 0; header = 8; need = 0;
        if (pos + 8 > s.Length) { need = pos + 16; return false; }
        var p = (int)pos;
        size = BinaryPrimitives.ReadUInt32BigEndian(s[p..]);
        for (var i = 4; i < 8; i++)
        {
            var b = s[p + i];
            if (b < 0x20 || b > 0x7E) { need = -1; return false; }
        }
        type = System.Text.Encoding.ASCII.GetString(s.Slice(p + 4, 4));
        if (size == 1)
        {
            header = 16;
            if (pos + 16 > s.Length) { need = pos + 16; return false; }
            var big = BinaryPrimitives.ReadUInt64BigEndian(s[(p + 8)..]);
            if (big > (ulong)(MaxFragmentBytes * 4)) { need = -1; return false; }
            size = (long)big;
        }
        if (size < header) { need = -1; return false; } // also rejects size 0 ("to end of file"): an unbounded box is not a first fragment
        return true;
    }

    private static bool HasTraf(ReadOnlySpan<byte> children)
    {
        var p = 0;
        while (p + 8 <= children.Length)
        {
            var sz = BinaryPrimitives.ReadUInt32BigEndian(children[p..]);
            if (sz < 8 || p + sz > children.Length) return false;
            if (children[(p + 4)..(p + 8)].SequenceEqual("traf"u8)) return true;
            p += (int)sz;
        }
        return false;
    }

    private static FirstFragmentInfo Bad(FragmentStatus status, string error) => new(status, 0, 0, 0, error);
    private static FirstFragmentInfo More(long required) => new(FragmentStatus.NeedMore, 0, 0, required);
}
