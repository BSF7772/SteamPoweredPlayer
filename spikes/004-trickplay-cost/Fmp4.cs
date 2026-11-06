// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S4: minimal fragmented-MP4 reader for Steam DASH chunks. Throwaway code.
// Observed layout of a Steam chunk: [moof mdat] per frame (one sample per fragment), the first fragment holds the IDR.
using System.Buffers.Binary;

namespace TrickplaySpike;

/// <summary>The first moof+mdat fragment of a chunk (the IDR), located inside a chunk head.</summary>
public sealed record FirstFragment(
    int MoofStart, int MoofSize, int MdatSize, int SampleCount, long FirstSampleOffset, int FirstSampleSize, bool FirstIsSync)
{
    /// <summary>Bytes from the start of the file that contain the whole first fragment (what the prefix read must cover).</summary>
    public long RequiredPrefix => (long)MoofStart + MoofSize + MdatSize;
}

public static class Fmp4
{
    static uint U32(ReadOnlySpan<byte> s, int o) => BinaryPrimitives.ReadUInt32BigEndian(s[o..]);
    static string Type(ReadOnlySpan<byte> s, int o) => System.Text.Encoding.ASCII.GetString(s.Slice(o + 4, 4));

    /// <summary>
    /// Locates moof + the following mdat at the start of a chunk. The head may be shorter than the fragment: the result then
    /// still carries RequiredPrefix so the caller can tell how much was missing. Returns null + error on anything unexpected.
    /// </summary>
    public static FirstFragment? Parse(ReadOnlySpan<byte> head, out string error)
    {
        error = "";
        int pos = 0;
        while (pos + 8 <= head.Length)
        {
            long size = U32(head, pos);
            string type = Type(head, pos);
            if (size == 1) { error = $"largesize box ({type}) not supported"; return null; }
            if (size < 8) { error = $"bad box size {size} at {pos} ({type})"; return null; }
            if (type == "moof")
            {
                if (pos + size > head.Length) { error = $"moof ({size} B) exceeds head ({head.Length} B)"; return null; }
                var moof = head.Slice(pos, (int)size);
                int mdatPos = pos + (int)size;
                if (mdatPos + 8 > head.Length) { error = "head ends before the mdat header"; return null; }
                if (Type(head, mdatPos) != "mdat") { error = $"box after moof is {Type(head, mdatPos)}, not mdat"; return null; }
                long mdatSize = U32(head, mdatPos);
                if (mdatSize < 8) { error = $"mdat size {mdatSize} unsupported"; return null; }
                var pm = ParseMoof(moof, out error);
                if (pm == null) return null;
                var (count, dataOffset, firstSize, firstSync) = pm.Value;
                return new FirstFragment(pos, (int)size, (int)mdatSize, count, pos + (long)dataOffset, firstSize, firstSync);
            }
            pos += (int)size;
        }
        error = "no moof in head";
        return null;
    }

    static (int Count, int DataOffset, int FirstSize, bool FirstSync)? ParseMoof(ReadOnlySpan<byte> moof, out string error)
    {
        error = "";
        int p = 8;
        while (p + 8 <= moof.Length)
        {
            int sz = (int)U32(moof, p);
            if (sz < 8) { error = "bad box size inside moof"; return null; }
            if (Type(moof, p) == "traf")
            {
                var traf = moof.Slice(p, sz);
                int q = 8;
                uint defSize = 0, defFlags = 0;
                bool haveTfhd = false;
                while (q + 8 <= traf.Length)
                {
                    int csz = (int)U32(traf, q);
                    if (csz < 8) { error = "bad box size inside traf"; return null; }
                    string cty = Type(traf, q);
                    var body = traf.Slice(q, csz);
                    if (cty == "tfhd")
                    {
                        haveTfhd = true;
                        uint tf = U32(body, 8) & 0xFFFFFF;
                        int o = 16; // size,type,ver/flags,track_id
                        if ((tf & 1) != 0) o += 8;
                        if ((tf & 2) != 0) o += 4;
                        if ((tf & 8) != 0) o += 4;
                        if ((tf & 0x10) != 0) { defSize = U32(body, o); o += 4; }
                        if ((tf & 0x20) != 0) { defFlags = U32(body, o); o += 4; }
                    }
                    else if (cty == "trun")
                    {
                        if (!haveTfhd) { error = "trun before tfhd"; return null; }
                        uint tr = U32(body, 8) & 0xFFFFFF;
                        int count = (int)U32(body, 12);
                        if (count <= 0) { error = "empty trun"; return null; }
                        int o = 16, dataOffset = 0;
                        if ((tr & 1) != 0) { dataOffset = (int)U32(body, o); o += 4; } else { error = "trun without data_offset"; return null; }
                        uint firstFlags = 0; bool hasFirstFlags = (tr & 4) != 0;
                        if (hasFirstFlags) { firstFlags = U32(body, o); o += 4; }
                        int entryLen = 0;
                        foreach (uint bit in new uint[] { 0x100, 0x200, 0x400, 0x800 }) if ((tr & bit) != 0) entryLen += 4;
                        int f = o;
                        if (o + entryLen > body.Length) { error = "trun truncated"; return null; }
                        if ((tr & 0x100) != 0) f += 4;
                        uint size = defSize;
                        if ((tr & 0x200) != 0) { size = U32(body, f); f += 4; }
                        uint flags = hasFirstFlags ? firstFlags : defFlags;
                        if ((tr & 0x400) != 0) flags = U32(body, f);
                        return (count, dataOffset, (int)size, (flags & 0x10000) == 0);
                    }
                    q += csz;
                }
            }
            p += sz;
        }
        error = "no traf/trun";
        return null;
    }

    /// <summary>Walks length-prefixed HEVC NAL units of a sample: NAL types, whether the lengths tile the sample exactly, VCL NAL count.</summary>
    public static (List<int> Types, bool Exact, int VclCount) WalkNals(ReadOnlySpan<byte> sample, int lengthSize = 4)
    {
        var types = new List<int>();
        int p = 0, vcl = 0;
        while (p + lengthSize <= sample.Length)
        {
            uint n = U32(sample, p);
            p += lengthSize;
            if (n == 0 || p + (long)n > sample.Length) return (types, false, vcl);
            int t = (sample[p] >> 1) & 0x3F;
            types.Add(t);
            if (t < 32) vcl++;
            p += (int)n;
        }
        return (types, p == sample.Length, vcl);
    }
}
