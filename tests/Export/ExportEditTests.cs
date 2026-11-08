// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Tests.Playback;

namespace SteamPoweredPlayer.Tests.Export;

public sealed class ExportEditRulesTests
{
    private static SessionMedia Media() => new("x", [new SegmentRun(1, 10)], 3.0, false, false);

    [Fact]
    public void Spans_are_sorted_and_merged()
    {
        var s = ExportSpans.Normalize([new(10, 12), new(1, 3), new(2, 4), new(5, 5), new(double.NaN, 9)]);
        Assert.Equal([new ExportSpan(1, 4), new ExportSpan(10, 12)], s);
    }

    [Fact]
    public void Lossless_spans_sharing_a_chunk_merge_so_no_chunk_is_written_twice()
    {
        var plans = ExportSpans.Plans(Media(), [new(1, 2), new(2.5, 3.5), new(20, 21)], lossless: true);
        Assert.Equal([(1, 2), (7, 7)], plans.Select(p => (p.FirstChunk, p.LastChunk)));
        Assert.Equal((0.0, 6.0), (plans[0].Start, plans[0].End));
    }

    [Fact]
    public void Encoded_spans_keep_their_exact_times()
    {
        var plans = ExportSpans.Plans(Media(), [new(1, 2), new(2.5, 3.5)], lossless: false);
        Assert.Equal(2, plans.Count);
        Assert.Equal(1.0, plans[0].Start);
    }

    [Fact]
    public void Highlights_clamp_and_merge()
    {
        var s = ExportSpans.Highlights(Media(), [2, 5, 20, 100]);
        Assert.Equal([new ExportSpan(0, 8), new ExportSpan(15, 23)], s);
    }

    [Fact]
    public void Chapters_map_through_cuts_and_speed_and_drop_what_was_cut()
    {
        var plans = ExportSpans.Plans(Media(), [new(0, 3), new(9, 12)], false);
        var c = ExportChapters.Layout(plans, [new(9, "b"), new(1, "a"), new(5, "gone"), new(1.0001, "same instant")], 2);
        Assert.Equal([new OutputChapter(0, 0.5, "Start"), new OutputChapter(0.5, 1.5, "a"), new OutputChapter(1.5, 3, "b")], c);
    }

    [Fact]
    public void Chapter_metadata_escapes_ffmpegs_special_characters()
    {
        var text = ExportChapters.Metadata([new OutputChapter(1.5, 2, "a=b;c#d\\e\nf")]);
        Assert.Equal(";FFMETADATA1\n[CHAPTER]\nTIMEBASE=1/1000\nSTART=1500\nEND=2000\ntitle=a\\=b\\;c\\#d\\\\e\\\nf\n", text);
    }

    [Theory]
    [InlineData(0.3, AudioSync.Confidence.High, SyncVerdict.Fix, -0.3)]
    [InlineData(-0.2, AudioSync.Confidence.High, SyncVerdict.Fix, 0.2)]
    [InlineData(0.03, AudioSync.Confidence.High, SyncVerdict.InSync, 0)]
    [InlineData(0.3, AudioSync.Confidence.Low, SyncVerdict.NotApplied, 0)]
    [InlineData(0.3, AudioSync.Confidence.None, SyncVerdict.NotApplied, 0)]
    public void Only_a_confident_detection_gives_a_number(double late, AudioSync.Confidence confidence, SyncVerdict verdict, double delay)
    {
        var advice = SyncAdvice.From(new AudioSync.Result(late, confidence, 3, 3));
        Assert.Equal(verdict, advice.Verdict);
        Assert.Equal(delay, advice.AudioDelay, 6);
    }

    [Fact]
    public void Samples_stay_inside_the_kept_spans()
    {
        var media = Media();
        var plans = ExportSpans.Plans(media, [new(0, 3), new(15, 30)], false);
        var samples = ExportService.SamplePlans(media, plans);
        Assert.Equal(2, samples.Count);
        Assert.All(samples, s => Assert.Contains(plans, p => s.Start >= p.Start && s.End <= p.End));
    }
}

public sealed class ExportQueueTests
{
    private static ExportJob Job(string name) =>
        new(new SessionMedia("x", [], 3, false, false), [], new EncodeSettings(), new EditSettings(), Path.Combine(Path.GetTempPath(), "spp-none-" + name), name);

    private static async Task DrainAsync(ExportQueue q)
    {
        while (q.Pending > 0) await Task.Delay(5);
        await q.IdleAsync();
    }

    [Fact]
    public async Task Runs_in_order_one_at_a_time_and_survives_a_failure()
    {
        var ran = new List<string>();
        int running = 0, peak = 0;
        var q = new ExportQueue(async (job, _, _) =>
        {
            peak = Math.Max(peak, Interlocked.Increment(ref running));
            await Task.Delay(10);
            lock (ran) ran.Add(job.Stem);
            Interlocked.Decrement(ref running);
            if (job.Stem == "b") throw new IOException("disk full");
            return new ExportResult(job.Stem, 1, []);
        });
        var (a, b, c) = (q.Enqueue(Job("a"), "a"), q.Enqueue(Job("b"), "b"), q.Enqueue(Job("c"), "c"));
        await DrainAsync(q);
        Assert.Equal(["a", "b", "c"], ran);
        Assert.Equal(1, peak);
        Assert.Equal((ExportItemState.Done, ExportItemState.Failed, ExportItemState.Done), (a.State, b.State, c.State));
        Assert.Equal("disk full", b.Error!.Message);
    }

    [Fact]
    public async Task Cancelling_a_waiting_item_skips_it_and_cancelling_the_running_one_stops_it()
    {
        var started = new TaskCompletionSource();
        var q = new ExportQueue(async (_, _, ct) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return new ExportResult("", 0, []);
        });
        var running = q.Enqueue(Job("a"), "a");
        var waiting = q.Enqueue(Job("b"), "b");
        await started.Task;
        q.Cancel(waiting);
        Assert.Equal(ExportItemState.Cancelled, waiting.State);
        q.Cancel(running);
        await DrainAsync(q);
        Assert.Equal(ExportItemState.Cancelled, running.State);
    }

    [Fact]
    public void Stale_work_folders_go_and_everything_else_stays()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spp-stale-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Path.Combine(dir, ".spp-export-dead"));
        Directory.CreateDirectory(Path.Combine(dir, "my-videos"));
        File.WriteAllText(Path.Combine(dir, ".spp-export-note.txt"), "a file, not ours");
        ExportService.RemoveStaleWork(dir);
        Assert.False(Directory.Exists(Path.Combine(dir, ".spp-export-dead")));
        Assert.True(Directory.Exists(Path.Combine(dir, "my-videos")));
        Assert.True(File.Exists(Path.Combine(dir, ".spp-export-note.txt")));
        Directory.Delete(dir, true);
    }
}

/// <summary>Cuts, edits, chapters and estimates end to end through ffmpeg on the synthetic clip (absolute tfdt 585 s, 12 s long).</summary>
public sealed class ExportEditEndToEndTests : IDisposable
{
    private const double O = ClipAxisMpvTests.Origin;
    private readonly string _clip = ClipAxisMpvTests.GenerateClip();
    private readonly string _out = Path.Combine(Path.GetTempPath(), "spp-edit-out-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        foreach (var d in new[] { _clip, _out })
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (IOException) { }
    }

    private ExportJob Job(EncodeSettings s, params ExportSpan[] spans) =>
        new(new SessionMedia(_clip, [new SegmentRun(1, 4)], 3.0, false, false, O), spans, s, new EditSettings(), _out, "out");

    private static EncodeSettings Copy => new() { Encoder = "copy", Audio = "copy" };
    private static EncodeSettings X264 => new() { Encoder = "x264", Quality = 30, Speed = 0, Audio = "aac", AudioKbps = 64 };

    [Fact]
    public async Task Two_lossless_spans_join_and_drop_the_cut()
    {
        var r = await ExportService.RunAsync(Job(Copy, new(O, O + 3), new(O + 9, O + 12)), null, default);
        Assert.InRange((await Ff.ProbeAsync(r.Path))!.Duration, 5.8, 6.3);
    }

    [Fact]
    public async Task Two_encoded_spans_trim_exactly_and_join()
    {
        var r = await ExportService.RunAsync(Job(X264, new(O + 1, O + 2.5), new(O + 8, O + 10)), null, default);
        Assert.InRange((await Ff.ProbeAsync(r.Path))!.Duration, 3.3, 3.7);
    }

    [Fact]
    public async Task Speed_shortens_the_output_and_mute_drops_the_sound()
    {
        var job = Job(X264, new ExportSpan(O, O + 6)) with { Edit = new EditSettings { Speed = 2, Mute = true } };
        var info = await Ff.ProbeAsync((await ExportService.RunAsync(job, null, default)).Path);
        Assert.InRange(info!.Duration, 2.8, 3.2);
        Assert.False(info.HasAudio);
    }

    [Fact]
    public async Task Markers_become_chapters_on_the_output_axis()
    {
        var job = Job(X264, new(O, O + 2), new(O + 8, O + 10)) with
        {
            Chapters = [new ExportChapter(O + 1, "Kill = one"), new ExportChapter(O + 5, "cut away"), new ExportChapter(O + 9, "Win")],
        };
        var r = await ExportService.RunAsync(job, null, default);
        var psi = new ProcessStartInfo(Ff.Ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-show_entries", "chapter=start_time:chapter_tags=title", "-of", "csv=p=0", r.Path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var lines = (await p.StandardOutput.ReadToEndAsync()).Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        Assert.Equal(["0.000000,Start", "1.000000,Kill = one", "3.000000,Win"], lines);
    }

    [Fact]
    public async Task Estimates_are_exact_for_lossless_and_close_for_encodes()
    {
        var lossless = await ExportService.EstimateAsync(Job(Copy, new ExportSpan(O, O + 6)), default);
        var chunks = new[] { 1, 2 }.SelectMany(c => new[] { 0, 1 }.Select(st => new FileInfo(Path.Combine(_clip, FolderNames.ChunkFile(st, c))).Length)).Sum();
        Assert.Equal(chunks, lossless);
        var estimated = await ExportService.EstimateAsync(Job(X264, new ExportSpan(O, O + 12)), default);
        var actual = (await ExportService.RunAsync(Job(X264, new ExportSpan(O, O + 12)), null, default)).Bytes;
        Assert.InRange(estimated, actual / 2, actual * 2);
    }

    [Fact]
    public async Task Queue_runs_a_real_export_and_reports_it()
    {
        var q = new ExportQueue();
        var item = q.Enqueue(Job(Copy, new ExportSpan(O, O + 3)), "clip");
        while (q.Pending > 0) await Task.Delay(5);
        await q.IdleAsync();
        Assert.Equal(ExportItemState.Done, item.State);
        Assert.True(File.Exists(item.Result!.Path));
    }
}
