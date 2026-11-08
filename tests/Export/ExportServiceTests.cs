// SPDX-License-Identifier: GPL-3.0-or-later
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Tests.Playback;

namespace SteamPoweredPlayer.Tests.Export;

public sealed class ExportPlanTests
{
    private static SessionMedia Media(double shift = 0, params SegmentRun[] runs) =>
        new("x", runs.Length > 0 ? runs : [new SegmentRun(1, 10)], 3.0, false, false, shift);

    [Fact]
    public void Range_inside_chunks_snaps_outward_and_keeps_the_exact_times()
    {
        var p = ExportPlan.For(Media(), 4, 8)!;
        Assert.Equal((2, 3), (p.FirstChunk, p.LastChunk));
        Assert.Equal((3.0, 9.0), (p.SnappedStart, p.SnappedEnd));
        Assert.Equal((4.0, 8.0), (p.Start, p.End));
    }

    [Fact]
    public void Range_on_the_grid_takes_no_extra_chunk()
    {
        var p = ExportPlan.For(Media(), 3, 6)!;
        Assert.Equal((2, 2), (p.FirstChunk, p.LastChunk));
    }

    [Fact]
    public void Clip_axis_shift_is_respected()
    {
        var p = ExportPlan.For(Media(585), 586, 590)!;
        Assert.Equal((1, 2), (p.FirstChunk, p.LastChunk));
        Assert.Equal((585.0, 591.0), (p.SnappedStart, p.SnappedEnd));
    }

    [Fact]
    public void Range_is_clamped_to_the_chunks_on_disk()
    {
        var p = ExportPlan.For(Media(0, new SegmentRun(3, 4)), 0, 1000)!;
        Assert.Equal((3, 4), (p.FirstChunk, p.LastChunk));
        Assert.Equal((6.0, 12.0), (p.Start, p.End));
    }

    [Fact]
    public void Gap_inside_the_range_is_reported()
    {
        var p = ExportPlan.For(Media(0, new SegmentRun(1, 2), new SegmentRun(5, 6)), 0, 18)!;
        Assert.Equal(6.0, p.GapSeconds);
    }

    [Theory]
    [InlineData(5.0, 5.0)]
    [InlineData(7.0, 5.0)]
    [InlineData(double.NaN, 5.0)]
    [InlineData(100.0, 200.0)]
    public void Empty_or_missing_range_has_no_plan(double start, double end) => Assert.Null(ExportPlan.For(Media(), start, end));

    [Fact]
    public void Whole_covers_every_run() => Assert.Equal((1, 10), (ExportPlan.Whole(Media())!.FirstChunk, ExportPlan.Whole(Media())!.LastChunk));

    [Fact]
    public void Trim_is_relative_to_the_real_tfdt_not_the_grid()
    {
        var plan = new ExportPlan(1, 3, 586, 590, 585, 594, 0);
        var (start, end) = ExportService.OutputTrim(plan, 585.25, 8.75);
        Assert.Equal(0.75, start, 6);
        Assert.Equal(4.75, end!.Value, 6);
    }

    [Fact]
    public void Trim_reaching_the_file_end_is_open()
    {
        var plan = new ExportPlan(1, 3, 585, 594, 585, 594, 0);
        var (start, end) = ExportService.OutputTrim(plan, 585.05, 8.9);
        Assert.Equal(0, start);
        Assert.Null(end);
    }

    [Theory]
    [InlineData("Game: A/B?  ·  Clip.", "Game A B ·  Clip")]
    [InlineData("...", "Export")]
    public void Stem_is_safe(string name, string expected) =>
        Assert.Equal(expected.Replace("  ", " "), ExportPaths.SafeStem(name));

    [Theory]
    [InlineData(@"C:\Rec", @"C:\rec\", true)]
    [InlineData(@"C:\Rec\clips\a", @"C:\REC", true)]
    [InlineData(@"C:\Recordings", @"C:\Rec", false)]
    [InlineData(@"D:\Out", null, false)]
    public void Destination_inside_steams_folder_is_detected(string folder, string? root, bool inside) =>
        Assert.Equal(inside, ExportPaths.IsInside(folder, root));
}

/// <summary>End to end through ffmpeg on the synthetic clip (Steam naming, absolute tfdt 585 s, 12 s long).</summary>
public sealed class ExportServiceTests : IDisposable
{
    private readonly string _clip = ClipAxisMpvTests.GenerateClip();
    private readonly string _out = Path.Combine(Path.GetTempPath(), "spp-export-out-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        foreach (var d in new[] { _clip, _out })
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (IOException) { }
    }

    private SessionMedia Media() => new(_clip, [new SegmentRun(1, 4)], 3.0, false, false, ClipAxisMpvTests.Origin);

    private ExportJob Job(double start, double end, EncodeSettings s) => new(Media(), [new ExportSpan(start, end)], s, new EditSettings(), _out, "out");

    private static EncodeSettings Copy => new() { Encoder = "copy", Audio = "copy" };
    private static EncodeSettings X264 => new() { Encoder = "x264", Quality = 30, Speed = 0, Audio = "aac", AudioKbps = 64 };

    [Fact]
    public async Task Lossless_starts_exactly_at_a_and_ends_on_b()
    {
        // Intentional change: it used to snap to the keyframe at the head (7 s); the lead-in is hidden by an edit list now (6 s).
        var o = ClipAxisMpvTests.Origin;
        var r = await ExportService.RunAsync(Job(o + 1, o + 7, Copy), null, default);
        var info = await Ff.ProbeAsync(r.Path);
        Assert.NotNull(info);
        Assert.Equal(6, info!.Duration, 0);          // A to B
        Assert.True(info.HasAudio);
        Assert.Equal([Path.GetFileName(r.Path)], Directory.GetFileSystemEntries(_out).Select(Path.GetFileName));   // no working files left
    }

    [Fact]
    public async Task Encode_trims_exactly()
    {
        var o = ClipAxisMpvTests.Origin;
        var r = await ExportService.RunAsync(Job(o + 1, o + 7, X264), null, default);
        var info = await Ff.ProbeAsync(r.Path);
        Assert.NotNull(info);
        Assert.InRange(info!.Duration, 5.85, 6.15);
        Assert.True(info.HasAudio);
    }

    [Fact]
    public async Task A_vanished_chunk_fails_with_chunk_gone_and_leaves_nothing()
    {
        File.Delete(Path.Combine(_clip, FolderNames.ChunkFile(1, 2)));
        await Assert.ThrowsAsync<ChunkGoneException>(() => ExportService.RunAsync(Job(ClipAxisMpvTests.Origin, ClipAxisMpvTests.Origin + 12, Copy), null, default));
        Assert.Empty(Directory.GetFileSystemEntries(_out));
    }

    [Fact]
    public async Task Cancel_leaves_nothing()
    {
        using var cts = new CancellationTokenSource();
        var progress = new SyncProgress(_ => cts.Cancel());
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => ExportService.RunAsync(Job(ClipAxisMpvTests.Origin, ClipAxisMpvTests.Origin + 12, X264), progress, cts.Token));
        Assert.Empty(Directory.GetFileSystemEntries(_out));
    }

    [Fact]
    public async Task Destination_inside_steams_folder_is_refused()
    {
        var job = Job(ClipAxisMpvTests.Origin, ClipAxisMpvTests.Origin + 3, Copy) with { OutputFolder = Path.Combine(_clip, "sub"), ForbiddenRoot = _clip };
        await Assert.ThrowsAsync<InvalidOperationException>(() => ExportService.RunAsync(job, null, default));
        Assert.False(Directory.Exists(Path.Combine(_clip, "sub")));
    }

    private sealed class SyncProgress(Action<ExportProgress> a) : IProgress<ExportProgress>
    {
        public void Report(ExportProgress value) => a(value);
    }
}
