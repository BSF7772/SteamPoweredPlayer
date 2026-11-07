// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core;
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class TrickplayIndexTests
{
    private static TrickplayManifest Manifest(params int[][] sheets) =>
        new(TrickplayManifest.CurrentVersion, 160, 90, 10, 10, 3.0,
            [.. sheets.Select((c, i) => new TrickplaySheet($"sheet-{i:0000}.jpg", c))],
            [new MissingChunk(7, MissingReason.ZeroFilled)]);

    [Fact]
    public void Manifest_round_trips_through_json_and_disk()
    {
        var m = Manifest([1, 2, 3], [4, 0, 6]);
        var back = TrickplayManifest.FromJson(m.ToJson());
        Assert.NotNull(back);
        Assert.Equal(m.ToJson(), back.ToJson());
        Assert.Equal([4, 0, 6], back.Sheets[1].Chunks);
        Assert.Equal(new MissingChunk(7, MissingReason.ZeroFilled), back.Missing[0]);
        Assert.Contains("\"ZeroFilled\"", m.ToJson());

        using var t = new TempDir();
        m.Write(t.Sub("k"));
        Assert.Equal(m.ToJson(), TrickplayManifest.TryRead(t.Sub("k"))!.ToJson());
        Assert.False(File.Exists(Path.Combine(t.Sub("k"), "manifest.json.tmp")));
        Assert.Null(TrickplayManifest.TryRead(t.Sub("none")));
    }

    [Theory]
    [InlineData("")]
    [InlineData("{not json")]
    [InlineData("{\"version\":99,\"tileWidth\":160,\"tileHeight\":90,\"columns\":10,\"rows\":10,\"intervalSeconds\":3,\"sheets\":[],\"missing\":[]}")]
    [InlineData("{\"version\":1,\"tileWidth\":160,\"tileHeight\":90,\"columns\":0,\"rows\":10,\"intervalSeconds\":3,\"sheets\":[],\"missing\":[]}")]
    public void Unusable_manifests_are_null(string json) => Assert.Null(TrickplayManifest.FromJson(json));

    [Fact]
    public void Lookup_gives_sheet_and_tile_rectangle()
    {
        // chunk 1 = tile 0 of sheet 0 (t 0-3 s); chunk 12 = tile 1 of sheet 1
        var idx = new TrickplayIndex(Manifest(Enumerable.Range(1, 10).Select(i => i == 3 ? 0 : i).Concat(Enumerable.Range(11, 90)).ToArray(), [101, 102]), @"C:\d\trick\k");
        var t = idx.Find(0.5)!;
        Assert.Equal(1, t.Chunk);
        Assert.Equal(@"C:\d\trick\k\sheet-0000.jpg", t.SheetPath);
        Assert.Equal((0, 0, 160, 90), (t.X, t.Y, t.Width, t.Height));

        var t11 = idx.Find(10 * 3.0 + 1)!; // chunk 11 = tile 10 = column 0, row 1
        Assert.Equal(11, t11.Chunk);
        Assert.Equal((0, 90), (t11.X, t11.Y));

        var t102 = idx.FindChunk(102)!;
        Assert.EndsWith("sheet-0001.jpg", t102.SheetPath);
        Assert.Equal((160, 0), (t102.X, t102.Y));
    }

    [Fact]
    public void Gaps_and_placeholder_tiles_have_no_exact_match_but_a_nearest_one()
    {
        var idx = new TrickplayIndex(Manifest([1, 2, 0, 4, 5, 10]), "d");
        Assert.Equal(5, idx.Count);
        Assert.Null(idx.Find(2 * 3.0 + 1));              // chunk 3: placeholder tile
        Assert.Null(idx.Find(7 * 3.0 + 1));              // chunk 8: not generated
        Assert.Equal(2, idx.FindNearest(2 * 3.0 + 1)!.Chunk);  // tie 2 vs 4 -> earlier
        Assert.Equal(5, idx.FindNearest(6 * 3.0 + 1)!.Chunk);  // chunk 7: 5 is 2 away, 10 is 3 away
        Assert.Equal(10, idx.FindNearest(8 * 3.0 + 1)!.Chunk); // chunk 9
        Assert.Equal(10, idx.FindNearest(1000)!.Chunk);
    }

    [Fact]
    public void Trimmed_head_resolves_to_the_first_available_thumbnail()
    {
        var idx = new TrickplayIndex(Manifest([1801, 1802, 1803]), "d"); // Steam trimmed chunks 1..1800
        Assert.Null(idx.Find(10));
        Assert.Equal(1801, idx.FindNearest(10)!.Chunk);
        Assert.Equal(1801, idx.Find(1800 * 3.0 + 0.1)!.Chunk);
        Assert.Equal(0, idx.ChunkAt(-5) - 1);
    }

    [Fact]
    public void Empty_index_finds_nothing() => Assert.Null(new TrickplayIndex(Manifest(), "d").FindNearest(5));

    [Fact]
    public void Plan_rebuilds_the_open_last_sheet_only_when_there_are_new_chunks()
    {
        var full = Enumerable.Range(1, 100).ToArray();
        var m = Manifest(full, [101, 102, 103]);
        Assert.Empty(Trickplay.Plan(m, Enumerable.Range(1, 103)).Work);

        var p = Trickplay.Plan(m, Enumerable.Range(50, 60)); // chunks 50..109: head trimmed, 104..109 are new
        Assert.Equal(6, p.NewChunks);
        Assert.Equal(3, p.Rebuilt);
        Assert.Single(p.KeptSheets);
        Assert.Equal([101, 102, 103, 104, 105, 106, 107, 108, 109], p.Work);
    }

    [Fact]
    public void Plan_drops_chunks_of_the_open_sheet_that_steam_removed_and_retries_missing_ones()
    {
        var m = Manifest([1, 2, 3]) with { Missing = [new MissingChunk(9, MissingReason.Truncated)] };
        var p = Trickplay.Plan(m, [3, 9, 10]);
        Assert.Equal([3, 9, 10], p.Work);
        Assert.Equal(1, p.Rebuilt);
        Assert.Equal(2, p.NewChunks);
        Assert.Empty(p.KeptSheets);
    }

    [Fact]
    public void MapFrames_uses_order_when_counts_match_and_pts_when_a_frame_was_lost()
    {
        Assert.Equal([5, 6, 7], Trickplay.MapFrames([5, 6, 7], [12.0, 15.0, 18.0], 3.0));
        // chunk 6 failed to decode: 2 frames for 3 fed chunks; pts 12 -> chunk 5, 18 -> chunk 7
        Assert.Equal([5, 7], Trickplay.MapFrames([5, 6, 7], [12.0, 18.2], 3.0));
        Assert.Equal([0, 7], Trickplay.MapFrames([5, 6, 7], [double.NaN, 18.0], 3.0));
    }

    [Fact]
    public void Ffmpeg_arguments_are_invariant_and_mode_dependent()
    {
        var gpu = Trickplay.BuildFfmpegArgs(DecodeMode.Gpu, 160, 0, 10, 10, 10, 3, "o-%04d.jpg");
        var cpu = Trickplay.BuildFfmpegArgs(DecodeMode.Cpu, 160, 90, 10, 10, 10, 0, "o-%04d.jpg");
        Assert.Contains("d3d11va", gpu);
        Assert.DoesNotContain("d3d11va", cpu);
        Assert.Contains("nokey", cpu);
        Assert.Contains("scale=160:-2:flags=fast_bilinear,format=yuvj420p,showinfo,tile=10x10", gpu);
        Assert.Contains("scale=160:90:flags=fast_bilinear,format=yuvj420p,showinfo,tile=10x10", cpu);
        Assert.Equal("3", gpu[gpu.ToList().IndexOf("-start_number") + 1]);
    }

    [Fact]
    public void Jpeg_size_is_read_from_the_sof_marker()
    {
        byte[] jpeg = [0xFF, 0xD8, 0xFF, 0xE0, 0, 4, 0, 0, 0xFF, 0xC0, 0, 11, 8, 0x03, 0x84, 0x06, 0x40, 3, 0, 0, 0];
        Assert.True(Trickplay.TryGetJpegSize(jpeg, out var w, out var h));
        Assert.Equal((1600, 900), (w, h));
        Assert.False(Trickplay.TryGetJpegSize([1, 2, 3, 4, 5], out _, out _));
    }

    [Theory]
    [InlineData("bg_730_20260101_120000", true)]
    [InlineData("..", false)]
    [InlineData("a/b", false)]
    [InlineData("a\\b", false)]
    [InlineData("", false)]
    public void Recording_keys_are_folder_safe(string key, bool ok) => Assert.Equal(ok, Trickplay.IsSafeKey(key));
}
