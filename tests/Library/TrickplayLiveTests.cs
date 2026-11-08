// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using SteamPoweredPlayer.Core;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;
using Xunit.Abstractions;
using CoreSession = SteamPoweredPlayer.Core.Diagnostics.SyntheticSession;

namespace SteamPoweredPlayer.Tests.Library;

/// <summary>Trickplay for a session Steam is still writing: tiles must follow the growing cache, one chunk behind the newest.</summary>
[Collection("ffmpeg")]
public sealed class TrickplayLiveTests(SyntheticSession session, ITestOutputHelper output) : IClassFixture<SyntheticSession>
{
    private static SessionMedia Live(string folder, int first, int last, bool live = true) =>
        new(folder, [new SegmentRun(first, last)], SessionMedia.DefaultSegmentSeconds, Live: live, Temporary: true);

    private static readonly TrickplayOptions Cpu = new(DecodeMode.Cpu, TileWidth: 160);

    private bool CanRun()
    {
        if (!session.Available) { output.WriteLine("ffmpeg not found: test skipped"); return false; }
        Assert.Null(session.Error);
        return true;
    }

    private static void AddChunk(SyntheticSession s, string folder, int n) =>
        File.Copy(Path.Combine(s.Folder, FolderNames.ChunkFile(0, n)), Path.Combine(folder, FolderNames.ChunkFile(0, n)));

    [Fact]
    public void A_live_session_loses_only_its_newest_chunk()
    {
        var m = Live("x", 5, 12);
        var b = TrickplayLive.Buildable(m)!;
        Assert.Equal(Enumerable.Range(5, 7), b.Chunks());
        Assert.True(b.Live);
        Assert.Equal(m.AxisShiftSeconds, b.AxisShiftSeconds);
    }

    [Fact]
    public void A_finished_session_is_built_whole()
    {
        var m = Live("x", 1, 12, live: false);
        Assert.Same(m, TrickplayLive.Buildable(m));
    }

    [Fact]
    public void A_live_session_with_one_chunk_or_a_one_chunk_last_run_drops_that_chunk()
    {
        Assert.Null(TrickplayLive.Buildable(Live("x", 7, 7)));
        var runs = new SessionMedia("x", [new SegmentRun(1, 5), new SegmentRun(9, 9)], 3, Live: true, Temporary: true);
        var b = TrickplayLive.Buildable(runs)!;
        Assert.Equal([new SegmentRun(1, 5)], b.Runs);
        Assert.Null(TrickplayLive.Buildable(new SessionMedia("x", [], 3, Live: true, Temporary: true)));
    }

    [Fact]
    public async Task Tiles_follow_a_growing_session_one_chunk_behind_and_only_new_ones_are_decoded()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var data = DataFolder.Open(t.Sub("data"), t.Sub("steam", "rec"));
        var folder = session.CopyTo(t.Sub("steam", "video", "bg_live"), 12);

        var firstMedia = TrickplayLive.Buildable(Live(folder, 1, 12))!;
        var sw = Stopwatch.StartNew();
        var first = await Trickplay.GenerateAsync(data, "k", firstMedia, Cpu);
        output.WriteLine($"initial {first.NewChunks} tiles in {sw.ElapsedMilliseconds} ms");
        Assert.Equal(11, first.NewChunks);
        var dir = Trickplay.DirFor(data, "k");
        Assert.Equal(Enumerable.Range(1, 11), TrickplayManifest.TryRead(dir)!.Sheets.Single().Chunks);

        var latencies = new List<double>();
        for (var n = 13; n <= 16; n++)
        {
            AddChunk(session, folder, n);
            sw.Restart();
            var r = await Trickplay.GenerateAsync(data, "k", TrickplayLive.Buildable(Live(folder, 1, n))!, Cpu);
            latencies.Add(sw.Elapsed.TotalMilliseconds);
            // chunk n is the open one: the tile that appears is chunk n - 1, and nothing older is decoded again beyond the open sheet's refill
            Assert.Equal(1, r.NewChunks);
            Assert.Equal(1, r.SheetsWritten);
            Assert.Empty(r.Missing);
            Assert.Equal(Enumerable.Range(1, n - 1), TrickplayManifest.TryRead(dir)!.Sheets.Single().Chunks);
        }
        output.WriteLine($"tile latency after a new chunk (CPU, incl. ffmpeg start and sheet rebuild): {string.Join(", ", latencies.Select(l => l.ToString("F0")))} ms, budget 3000 ms (one chunk interval)");
        Assert.All(latencies, l => Assert.True(l < 3000, $"{l:F0} ms"));

        // the same media again is no work (the queue is woken by every index change, many of them leave the chunks unchanged)
        var idle = await Trickplay.GenerateAsync(data, "k", TrickplayLive.Buildable(Live(folder, 1, 16))!, Cpu);
        Assert.Equal((0, 0, 0), (idle.NewChunks, idle.Rebuilt, idle.SheetsWritten));
    }

    [Fact]
    public async Task Steam_trimming_the_head_while_tiles_are_built_does_not_lose_the_new_ones()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var data = DataFolder.Open(t.Sub("data"), t.Sub("steam", "rec"));
        var folder = session.CopyTo(t.Sub("steam", "video", "bg_live"), 12);
        await Trickplay.GenerateAsync(data, "k", TrickplayLive.Buildable(Live(folder, 1, 12))!, Cpu);

        AddChunk(session, folder, 13);
        AddChunk(session, folder, 14);
        CoreSession.DeleteChunk(folder, 1);   // Steam's head trim (stream 1 is not in the copy: DeleteChunk ignores it)
        CoreSession.DeleteChunk(folder, 2);
        var r = await Trickplay.GenerateAsync(data, "k", TrickplayLive.Buildable(Live(folder, 3, 14))!, Cpu);
        Assert.Equal(2, r.NewChunks);
        var m = TrickplayManifest.TryRead(Trickplay.DirFor(data, "k"))!;
        Assert.Equal(Enumerable.Range(3, 11), m.Sheets.Single().Chunks);
        Assert.Empty(m.Missing);
    }
}
