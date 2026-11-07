// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Text;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Tests.Fakes;

namespace SteamPoweredPlayer.Tests.Library;

/// <summary>
/// Phase 2.1 §1.1: saved clips restart chunk numbers at 1 but keep the session's absolute tfdt (real shortest clip: chunk 1 at 588.044 s
/// while ChunkStartSeconds(1) was 0). All boxes here are hand-built; no real media.
/// </summary>
public sealed class ClipAxisTests
{
    private const double Shift = 585;   // the clip in docs/steam-recording-format.md: chunk 1 starts at 585.0 s

    // ---- boxes -----------------------------------------------------------------------------------------------------

    private static byte[] FullBox(string type, byte version, byte[] payload) =>
        Boxes.Box(type, [version, 0, 0, 0], payload);

    private static byte[] U32(uint v) { var b = new byte[4]; BinaryPrimitives.WriteUInt32BigEndian(b, v); return b; }
    private static byte[] U64(ulong v) { var b = new byte[8]; BinaryPrimitives.WriteUInt64BigEndian(b, v); return b; }

    internal static byte[] Tfdt(ulong t, bool v1 = false) => v1 ? FullBox("tfdt", 1, U64(t)) : FullBox("tfdt", 0, U32((uint)t));

    internal static byte[] ChunkHead(ulong tfdt, bool v1 = false) =>
    [
        .. Boxes.Box("styp", "msdh"u8.ToArray()),
        .. Boxes.Box("moof", Boxes.Box("mfhd", new byte[8]), Boxes.Box("traf", Boxes.Box("tfhd", new byte[16]), Tfdt(tfdt, v1))),
        .. Boxes.Mdat(64),
    ];

    internal static byte[] Init(uint timescale, bool v1 = false)
    {
        var mdhd = v1
            ? FullBox("mdhd", 1, [.. new byte[16], .. U32(timescale), .. new byte[8], 0x55, 0xC4, 0, 0])
            : FullBox("mdhd", 0, [.. new byte[8], .. U32(timescale), .. new byte[4], 0x55, 0xC4, 0, 0]);
        return
        [
            .. Boxes.Box("ftyp", "iso6"u8.ToArray()),
            .. Boxes.Box("moov", Boxes.Box("mvhd", new byte[100]), Boxes.Box("trak", Boxes.Box("tkhd", new byte[84]), Boxes.Box("mdia", mdhd))),
        ];
    }

    private static SessionMedia Clip(int first = 1, int last = 4, double shift = Shift) =>
        new("C:/rec/clip_730_20250101_120000/video/bg_730_20250101_115000", [new SegmentRun(first, last)], 3.0, false, false, shift);

    // ---- tfdt parsing ----------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Tfdt_of_the_first_fragment_and_the_init_timescale_give_seconds(bool v1)
    {
        Assert.Equal(588_044_000UL, FirstFragment.BaseMediaDecodeTime(ChunkHead(588_044_000, v1)));
        Assert.Equal(1_000_000U, FirstFragment.Timescale(Init(1_000_000, v1)));
        Assert.Equal(588.044, ScanTfdt.Seconds(Init(1_000_000, v1), ChunkHead(588_044_000, v1))!.Value, 9);
    }

    [Fact]
    public void Tfdt_is_null_when_missing_truncated_or_zero_filled()
    {
        Assert.Null(FirstFragment.BaseMediaDecodeTime(Boxes.Fragment(10)));                  // traf without tfdt
        var head = ChunkHead(1000);
        Assert.Null(FirstFragment.BaseMediaDecodeTime(head.AsSpan(0, 40)));                  // moof cut off
        Assert.Null(FirstFragment.BaseMediaDecodeTime(new byte[64]));
        Assert.Null(FirstFragment.Timescale(Boxes.Box("ftyp", new byte[8])));
        Assert.Null(ScanTfdt.Seconds(Init(0), head));                                         // timescale 0 is not a timescale
    }

    // ---- the axis --------------------------------------------------------------------------------------------------

    [Theory]
    [InlineData(588.044, 1, 588)]      // real clips (redacted values): jitter of a few ms to 73 ms after a whole segment
    [InlineData(1005.073, 1, 1005)]
    [InlineData(585.0, 1, 585)]
    [InlineData(0.0, 1, 0)]            // a cache session from its start
    [InlineData(5517.038, 1840, 0)]    // a trimmed cache session: unchanged
    [InlineData(336.364, 113, 0)]      // 0.36 s of jitter at a trimmed head still rounds to 0
    [InlineData(double.NaN, 1, 0)]
    [InlineData(-5.0, 1, 0)]
    [InlineData(0.0, 10, 0)]           // never before the axis' zero: (10 − 1)·3 = 27 > 0 would need −27, clamped
    public void Axis_shift_is_the_tfdt_offset_rounded_to_whole_segments(double tfdt, int chunk, double expected)
    {
        var shift = SessionMedia.AxisShiftFor(tfdt, chunk, 3.0);
        Assert.Equal(chunk == 10 ? -27 : expected, shift);
        Assert.False(double.IsNegative(shift) && shift == 0);   // never −0
    }

    [Fact]
    public void Chunk_times_and_lookup_use_the_shift()
    {
        var m = Clip();
        Assert.Equal(585, m.ChunkStartSeconds(1));
        Assert.Equal(588, m.ChunkEndSeconds(1));
        Assert.Equal(585, m.TfdtOriginSeconds);
        Assert.Equal(1, m.ChunkAt(585));
        Assert.Equal(2, m.ChunkAt(588.5));
        Assert.Equal(4, m.ChunkAt(596.9));
        Assert.Equal(1, m.ChunkAt(double.NaN));
        var cache = m with { AxisShiftSeconds = 0 };
        Assert.Equal(0, cache.ChunkStartSeconds(1));
        Assert.Equal(1, cache.ChunkAt(0));
    }

    [Fact]
    public void Edl_lead_part_of_a_clip_reaches_its_real_tfdt()
    {
        var edl = Edl.Build(Clip(), [1, 2, 3, 4], p => p);
        Assert.Contains("chunk-stream0-00001.m4s,length=588\n", edl);
        Assert.Contains("chunk-stream1-00001.m4s,length=588\n", edl);
        Assert.Contains("chunk-stream0-00002.m4s,length=3\n", edl);
        // same as an unshifted session whose chunks are numbered from 196
        var cacheLike = new SessionMedia("C:/rec/x", [new SegmentRun(196, 199)], 3.0, false, true);
        Assert.Equal(cacheLike.ChunkStartSeconds(196), Clip().ChunkStartSeconds(1));
    }

    [Fact]
    public void Window_and_reload_policy_find_the_clip_chunks_on_the_tfdt_axis()
    {
        var m = new SessionMedia("C:/rec/clip/video/bg", [new SegmentRun(1, 100)], 3.0, false, false, Shift);
        var w = EdlWindow.Window(m, Shift + 150, 20);              // chunk 51
        Assert.Equal(46, w[0]);                                    // 51 − 20/4
        Assert.Equal(ReloadReason.None, EdlWindow.ShouldReload(w, m, Shift + 150, 0));
        Assert.Equal(ReloadReason.OutsideWindow, EdlWindow.ShouldReload(w, m, Shift + 30, 0));
        // The bug: on the old axis (shift 0) a clip playing at its real tfdt was "outside" every window.
        Assert.Equal(ReloadReason.OutsideWindow, EdlWindow.ShouldReload(EdlWindow.Window(m with { AxisShiftSeconds = 0 }, 150, 20),
            m with { AxisShiftSeconds = 0 }, Shift + 150, 0));
    }

    // ---- playback session on the clip axis -------------------------------------------------------------------------

    [Fact]
    public async Task Clip_ui_axis_runs_from_zero_to_its_duration_and_plus_five_lands_at_five()
    {
        var p = new FakeMpvPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(Clip());
        Assert.Equal(Shift, p.Loads[0].Start);                     // loadfile start= is the real tfdt of chunk 1
        Assert.Equal(Shift, s.MediaStartSeconds);
        Assert.Equal(12, s.UiDuration);
        Assert.Equal(0, s.UiPosition);

        p.Tick(Shift + 0.016);                                     // mpv time-pos = tfdt with the lead EDL
        Assert.Equal(0.016, s.UiPosition, 9);
        await s.SeekRelativeAsync(5);
        Assert.Equal(Shift + 5.016, p.Loads[^1].Start, 9);
        Assert.Equal(5.016, s.UiPosition, 9);

        await s.SeekUiAsync(100);                                  // past the end: just before it, never a jump to 0
        Assert.Equal(12 - PlaybackSession.EndMarginSeconds, s.UiPosition, 9);
    }

    [Fact]
    public async Task Open_sizes_the_demuxer_cache_from_the_recording_bitrate()
    {
        var p = new FakeMpvPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(Clip() with { BitsPerSecond = 50_000_000 });
        Assert.Equal(DemuxerCache.For(50_000_000), p.DemuxerCache);
    }

    // ---- markers ---------------------------------------------------------------------------------------------------

    [Fact]
    public void Clip_markers_are_placed_on_the_shifted_runs()
    {
        var entry = new TimelineEntry("1", 0, TimelineEntryType.Event, "event", "kill", null, "cs2_gun_kill", null, 0, false, null, [], null, null);
        var marker = new Marker(entry, Shift + 4.5, null, MarkerAlignment.Exact);
        Assert.Single(Markers.Place([marker], Clip()).Visible);
        // Regression: on the old axis every clip marker was trimmed away "after the end".
        var old = Markers.Place([marker], Clip() with { AxisShiftSeconds = 0 });
        Assert.Equal(TrimReason.AfterEnd, Assert.Single(old.TrimmedAway).Reason);
    }

    // ---- scanner + scan cache --------------------------------------------------------------------------------------

    private static FakeSession ClipFolder(ulong chunk1Tfdt)
    {
        var f = new FakeSession("bg_730_20250101_115000");
        f.Write("init-stream0.m4s", Init(1_000_000)).Write("init-stream1.m4s", Init(48_000));
        for (var n = 1; n <= 4; n++)
        {
            f.Write(FolderNames.ChunkFile(0, n), ChunkHead(chunk1Tfdt + (ulong)(n - 1) * 3_000_000));
            f.Write(FolderNames.ChunkFile(1, n), ChunkHead((chunk1Tfdt + (ulong)(n - 1) * 3_000_000) / 1_000_000 * 48_000));
        }
        return f;
    }

    [Fact]
    public async Task Scanner_reads_the_first_chunk_tfdt_and_remembers_it()
    {
        using var f = ClipFolder(588_044_000);
        var scan = await Scanner.ScanCachedAsync(f.Folder, null, new ScanOptions { Temporary = false });
        var media = scan.Result.Media!;
        Assert.Equal(588, media.AxisShiftSeconds);
        Assert.Equal(588, media.ChunkStartSeconds(1));
        Assert.True(media.BitsPerSecond > 0);
        var t = scan.Heads.Tfdt!.Value;
        Assert.Equal(1, t.Chunk);
        Assert.Equal(588.044, t.Seconds, 9);

        // Warm: the remembered value is used while the first chunk is unchanged (no read: a different value proves it).
        var json = Encoding.UTF8.GetString(ScanCache.From([new("k", scan.Heads)]).ToUtf8());
        var trusted = ScanCache.Parse(Encoding.UTF8.GetBytes(json)).Cache.Get("k")!;
        Assert.Equal(t, trusted.Tfdt);
        var planted = ScanCache.Parse(Encoding.UTF8.GetBytes(json.Replace("588.044", "900.01"))).Cache.Get("k")!;
        var warm = await Scanner.ScanCachedAsync(f.Folder, planted, new ScanOptions { Temporary = false });
        Assert.Equal(0, warm.HeadReads);
        Assert.Equal(900, warm.Result.Media!.AxisShiftSeconds);
    }

    [Fact]
    public async Task Old_scan_cache_entries_without_tfdt_are_read_once()
    {
        using var f = ClipFolder(585_000_000);
        var cold = await Scanner.ScanCachedAsync(f.Folder, null, new ScanOptions { Temporary = false });
        var json = Encoding.UTF8.GetString(ScanCache.From([new("k", cold.Heads)]).ToUtf8());
        var withoutTfdt = System.Text.RegularExpressions.Regex.Replace(json, ",\"tfdt\":\\[[^\\]]*\\]", "");
        Assert.DoesNotContain("tfdt", withoutTfdt);
        var old = ScanCache.Parse(Encoding.UTF8.GetBytes(withoutTfdt));
        Assert.Equal(ScanCacheStatus.Ok, old.Status);
        Assert.Null(old.Cache.Get("k")!.Tfdt);

        var warm = await Scanner.ScanCachedAsync(f.Folder, old.Cache.Get("k"), new ScanOptions { Temporary = false });
        Assert.Equal(0, warm.HeadReads);
        Assert.Equal(585, warm.Result.Media!.AxisShiftSeconds);
        Assert.NotNull(warm.Heads.Tfdt);
        Assert.False(warm.Heads.SameAs(old.Cache.Get("k")!));     // the cache is dirty, so the tfdt gets persisted
    }

    [Fact]
    public async Task Unreadable_tfdt_leaves_the_axis_unshifted()
    {
        using var f = new FakeSession();
        f.Init().Chunks(1, 3);                                     // fake box heads: no moof, no moov
        var scan = await Scanner.ScanCachedAsync(f.Folder, null);
        Assert.Equal(0, scan.Result.Media!.AxisShiftSeconds);
        Assert.Null(scan.Heads.Tfdt);
    }
}
