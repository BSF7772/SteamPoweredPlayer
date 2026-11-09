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

    /// <summary>Hash of every decoded frame with its pts, as a player shows them.</summary>
    private static async Task<List<(double Pts, string Md5)>> ShownFrames(string path)
    {
        if (!Ff.Available) Ff.Locate();
        var times = (await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "frame=pts_time", "-of", "csv=p=0", path])).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => double.Parse(l.TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        var md5 = (await Ff.RunAsync(Ff.Ffmpeg, ["-v", "error", "-i", path, "-map", "0:v:0", "-fps_mode", "passthrough", "-f", "framemd5", "-"])).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => !l.StartsWith('#')).Select(l => l.Split(',')[^1].Trim()).ToList();
        Assert.Equal(times.Count, md5.Count);
        return times.Zip(md5, (t, m) => (t, m)).ToList();
    }

    private async Task<List<(double Pts, string Md5)>> SourceFrames()
    {
        var path = Path.Combine(_clip, "whole-source.mp4");
        await using (var source = RangeSource.Create(Media(), 0, 1, 4))
        await using (var target = File.Create(path))
            await source.CopyToAsync(target);
        var frames = await ShownFrames(path);
        File.Delete(path);
        return frames;
    }

    [Theory]
    [InlineData(1.21, 7.37)]            // both off the 3 s grid
    [InlineData(3.0, 9.0)]              // both on it
    [InlineData(0.5, 2.9)]              // inside one chunk
    public async Task Lossless_shows_exactly_the_source_frames_in_a_b(double a, double b)
    {
        var o = ClipAxisMpvTests.Origin;
        var source = await SourceFrames();
        var r = await ExportService.RunAsync(Job(o + a, o + b, Copy), null, default);
        var expected = source.Where(f => f.Pts >= o + a - 0.001 && f.Pts < o + b).Select(f => f.Md5);
        Assert.Equal(expected, (await ShownFrames(r.Path)).Select(f => f.Md5));
    }

    [Fact]
    public async Task Lossless_with_cuts_shows_exactly_the_kept_frames_and_nothing_between()
    {
        var o = ClipAxisMpvTests.Origin;
        (double A, double B)[] kept = [(0.7, 1.4), (1.9, 2.6), (4.1, 4.9), (9.2, 11.3)];       // the first two share a chunk
        var source = await SourceFrames();
        var job = Job(0, 1, Copy) with { Spans = kept.Select(k => new ExportSpan(o + k.A, o + k.B)).ToList() };
        var r = await ExportService.RunAsync(job, null, default);
        var expected = kept.SelectMany(k => source.Where(f => f.Pts >= o + k.A - 0.001 && f.Pts < o + k.B)).Select(f => f.Md5);
        Assert.Equal(expected, (await ShownFrames(r.Path)).Select(f => f.Md5));
        var info = await Ff.ProbeAsync(r.Path);
        Assert.Equal(kept.Sum(k => k.B - k.A), info!.Duration, 0.05);
    }

    [Fact]
    public async Task Lossless_keeps_chapters_on_the_output_axis_under_cuts()
    {
        var o = ClipAxisMpvTests.Origin;
        var job = Job(0, 1, Copy) with
        {
            Spans = [new ExportSpan(o + 0.5, o + 1.5), new ExportSpan(o + 4, o + 6)],
            Chapters = [new ExportChapter(o + 1, "first"), new ExportChapter(o + 5, "second")],
        };
        var r = await ExportService.RunAsync(job, null, default);
        var starts = (await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-show_entries", "chapter=start_time", "-of", "csv=p=0", r.Path])).StdOut
            .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => double.Parse(l.TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(3, starts.Count);                 // "Start", then the two markers
        Assert.Equal(0.5, starts[1], 0.01);
        Assert.Equal(2.0, starts[2], 0.01);
    }

    [Theory]
    [InlineData(new[] { 0.0, 1.5, 3.0, 5.1 }, 5.2, true)]
    [InlineData(new[] { 0.0, 1.5, 1.5, 1.5 }, 5.2, false)]      // the real failure: every later frame on one instant
    [InlineData(new double[0], 5.2, false)]
    public void A_join_must_reach_the_kept_length(double[] times, double expected, bool whole) =>
        Assert.Equal(whole, ExportService.IsWholePicture(times, expected));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]                  // "Fix timing drift", on by default in the app
    public async Task Encoded_cuts_keep_the_whole_picture(bool fixDrift)
    {
        // Found by the verifier on a real clip: the picture stopped at 1.5 s of a 5.2 s span, with the later frames stamped 0.016 ms apart.
        var o = ClipAxisMpvTests.Origin;
        (double A, double B)[] kept = [(0.5, 2.0), (4.0, 5.5), (8.0, 10.2)];
        var job = Job(0, 1, X264) with { Spans = kept.Select(k => new ExportSpan(o + k.A, o + k.B)).ToList(), Edit = new EditSettings { FixSync = fixDrift } };
        var r = await ExportService.RunAsync(job, null, default);
        var frames = await ShownFrames(r.Path);
        var total = kept.Sum(k => k.B - k.A);
        Assert.InRange(frames[^1].Pts - frames[0].Pts, total - 0.15, total + 0.15);
        var gaps = frames.Zip(frames.Skip(1), (x, y) => y.Pts - x.Pts).ToList();
        Assert.True(gaps.Min() > 0.001, $"smallest frame gap {gaps.Min() * 1000:0.000} ms");
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
