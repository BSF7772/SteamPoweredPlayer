// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Tests.Library;

/// <summary>Hand-built ISO-BMFF boxes (no real media).</summary>
internal static class Boxes
{
    public static byte[] Box(string type, params byte[][] children)
    {
        var body = children.SelectMany(c => c).ToArray();
        var b = new byte[8 + body.Length];
        BinaryPrimitives.WriteUInt32BigEndian(b, (uint)b.Length);
        System.Text.Encoding.ASCII.GetBytes(type).CopyTo(b, 4);
        body.CopyTo(b, 8);
        return b;
    }

    public static byte[] Moof() => Box("moof", Box("mfhd", new byte[8]), Box("traf", Box("tfhd", new byte[16])));

    public static byte[] Mdat(int payload) => Box("mdat", Enumerable.Repeat((byte)0xAB, payload).ToArray());

    public static byte[] Fragment(int payload) => [.. Moof(), .. Mdat(payload)];
}

public sealed class FirstFragmentTests
{
    [Fact]
    public void Whole_fragment_inside_the_head_is_found_and_trailing_data_ignored()
    {
        var frag = Boxes.Fragment(1000);
        var head = frag.Concat(Boxes.Fragment(50)).ToArray();
        var r = FirstFragment.Find(head);
        Assert.Equal(FragmentStatus.Found, r.Status);
        Assert.Equal(0, r.Offset);
        Assert.Equal(frag.Length, r.Length);
        Assert.Equal(frag.Length, r.RequiredBytes);
    }

    [Fact]
    public void Boxes_before_the_moof_are_skipped_and_the_offset_reported()
    {
        var styp = Boxes.Box("styp", "msdh"u8.ToArray());
        var frag = Boxes.Fragment(300);
        var r = FirstFragment.Find([.. styp, .. frag]);
        Assert.Equal(FragmentStatus.Found, r.Status);
        Assert.Equal(styp.Length, r.Offset);
        Assert.Equal(frag.Length, r.Length);
        Assert.Equal(styp.Length + frag.Length, r.RequiredBytes);
    }

    [Fact]
    public void Larger_idr_than_the_head_says_exactly_how_many_bytes_are_needed()
    {
        var frag = Boxes.Fragment(500_000);
        var head = frag.AsSpan(0, FirstFragment.DefaultFirstRead).ToArray();
        var r = FirstFragment.Find(head);
        Assert.Equal(FragmentStatus.NeedMore, r.Status);
        Assert.Equal(frag.Length, r.RequiredBytes);

        var second = FirstFragment.Find(frag.AsSpan(0, (int)r.RequiredBytes));
        Assert.Equal(FragmentStatus.Found, second.Status);
    }

    [Theory]
    [InlineData(3)]
    [InlineData(8)]
    [InlineData(20)]
    public void Head_cut_inside_the_moof_asks_for_more_and_never_claims_found(int cut)
    {
        var frag = Boxes.Fragment(100);
        var r = FirstFragment.Find(frag.AsSpan(0, cut));
        Assert.Equal(FragmentStatus.NeedMore, r.Status);
        Assert.True(r.RequiredBytes > cut);
        // following the advice converges
        var next = FirstFragment.Find(frag.AsSpan(0, (int)Math.Min(r.RequiredBytes, frag.Length)));
        Assert.NotEqual(FragmentStatus.Invalid, next.Status);
    }

    [Fact]
    public void Head_ending_between_moof_and_mdat_header_needs_more()
    {
        var moof = Boxes.Moof();
        var r = FirstFragment.Find(moof);
        Assert.Equal(FragmentStatus.NeedMore, r.Status);
        Assert.True(r.RequiredBytes >= moof.Length + 8);
    }

    [Fact]
    public void Zero_filled_head_is_rejected()
    {
        Assert.Equal(FragmentStatus.ZeroFilled, FirstFragment.Find(new byte[4096]).Status);
        Assert.Equal(FragmentStatus.ZeroFilled, FirstFragment.Find(new byte[8]).Status);
    }

    [Fact]
    public void Empty_head_needs_more_and_garbage_is_invalid()
    {
        Assert.Equal(FragmentStatus.NeedMore, FirstFragment.Find([]).Status);
        var garbage = Enumerable.Range(0, 64).Select(i => (byte)(0xF0 + i % 8)).ToArray();
        Assert.Equal(FragmentStatus.Invalid, FirstFragment.Find(garbage).Status);
    }

    [Fact]
    public void Mdat_before_moof_and_moof_without_traf_and_non_mdat_after_moof_are_invalid()
    {
        Assert.Equal(FragmentStatus.Invalid, FirstFragment.Find(Boxes.Mdat(100)).Status);
        Assert.Equal(FragmentStatus.Invalid, FirstFragment.Find([.. Boxes.Box("moof", Boxes.Box("mfhd", new byte[8])), .. Boxes.Mdat(10)]).Status);
        Assert.Equal(FragmentStatus.Invalid, FirstFragment.Find([.. Boxes.Moof(), .. Boxes.Box("free", new byte[40])]).Status);
    }

    [Fact]
    public void Absurd_sizes_are_invalid_not_a_giant_allocation_request()
    {
        var frag = Boxes.Fragment(100);
        var bad = frag.ToArray();
        BinaryPrimitives.WriteUInt32BigEndian(bad.AsSpan(Boxes.Moof().Length), 0x7FFFFFFF); // mdat claims 2 GiB
        Assert.Equal(FragmentStatus.Invalid, FirstFragment.Find(bad).Status);
    }

    [Fact]
    public void Mdat_with_64_bit_size_is_understood()
    {
        var moof = Boxes.Moof();
        var payload = new byte[200];
        var mdat = new byte[16 + payload.Length];
        BinaryPrimitives.WriteUInt32BigEndian(mdat, 1);
        "mdat"u8.CopyTo(mdat.AsSpan(4));
        BinaryPrimitives.WriteUInt64BigEndian(mdat.AsSpan(8), (ulong)mdat.Length);
        var r = FirstFragment.Find([.. moof, .. mdat]);
        Assert.Equal(FragmentStatus.Found, r.Status);
        Assert.Equal(moof.Length + mdat.Length, r.Length);
    }
}
