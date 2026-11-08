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
        // Intentional change (edit-list start): the FIRST plan keeps its exact start (its lead-in is hidden); later ones start on their keyframe.
        Assert.Equal((1.0, 3.5), (plans[0].Start, plans[0].End));
        Assert.Equal(18.0, plans[1].Start);
    }

    [Fact]
    public void A_lossless_plan_counts_from_a_and_one_on_its_keyframe_counts_from_the_first_dts()
    {
        var inside = ExportPlan.For(Media(), 4.5, 8)!;                  // chunk 2 starts at 3 s
        Assert.Equal(4.499, ExportService.LosslessOrigin(inside, 2.967), 6);
        var onKeyframe = ExportPlan.For(Media(), 3, 8)!;
        Assert.Equal(2.967, ExportService.LosslessOrigin(onKeyframe, 2.967), 6);
        Assert.Equal(5.0, ExportService.LosslessOrigin(inside, 5.0), 6);   // a first packet after A is never cut away
    }

    [Theory]
    [InlineData(0, 3, 5)]               // one chunk of margin around chunks 4..4 (9..12 s)
    [InlineData(-5, 4, 7)]              // sound 5 s earlier reads later chunks
    [InlineData(5, 1, 3)]               // sound 5 s later reads earlier chunks, clamped to the recording
    public void Audio_chunks_cover_the_sync_shift_and_a_margin(double delay, int first, int last)
    {
        var plan = ExportPlan.For(Media(), 9.5, 11)!;
        Assert.Equal((first, last), ExportPlan.AudioChunks(Media(), plan, delay));
    }

    [Fact]
    public void Lossless_args_count_from_a_so_the_keyframe_is_negative_and_the_muxer_writes_an_edit_list()
    {
        // Intentional change: no -ss/make_zero on the picture any more (they shift everything, so no edit list).
        var audio = string.Join(' ', ExportService.LosslessAudioArgs("a", "c", 100, 106.5, -1.5));
        Assert.Contains("-copyts -itsoffset -101.5 -i a", audio);
        Assert.Contains("-ss 0 -to 6.5", audio);
        var video = string.Join(' ', ExportService.LosslessArgs("v", "c", "o", 100, 106.5));
        Assert.Contains("-itsoffset -100 -i v -i c", video);
        Assert.Contains("-to 6.5 -avoid_negative_ts disabled", video);
        Assert.DoesNotContain("-ss", video);
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
    public async Task Lossless_with_an_audio_delay_keeps_both_streams_and_the_length()
    {
        var job = Job(Copy, new ExportSpan(O, O + 6)) with { AudioDelay = 0.25 };
        var info = await Ff.ProbeAsync((await ExportService.RunAsync(job, null, default)).Path);
        Assert.True(info!.HasAudio);
        Assert.InRange(info.Duration, 5.5, 6.6);
    }

    private static async Task<Dictionary<string, (double Start, double End)>> StreamsAsync(string path)
    {
        var r = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_type,start_time,duration", "-of", "csv=p=0", path]);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        return r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(l => l.Split(',')).Where(p => p[0] is "video" or "audio")
            .ToDictionary(p => p[0], p => (double.Parse(p[1], inv), double.Parse(p[1], inv) + double.Parse(p[2], inv)));
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.8)]                  // the broken clip: the picture froze for 1.8 s and the end had no sound
    [InlineData(1.8)]
    public async Task Lossless_starts_both_streams_together_and_ends_exactly_on_b(double delay)
    {
        var job = Job(Copy, new ExportSpan(O + 3.5, O + 8.2)) with { AudioDelay = delay };
        var s = await StreamsAsync((await ExportService.RunAsync(job, null, default)).Path);
        Assert.InRange(s["video"].Start, 0, 0.12);                  // A: the lead-in from the keyframe at O + 3 is hidden by the edit list
        Assert.InRange(s["audio"].Start, 0, 0.12);
        // Intentional change: B is now 4.7 s after A (it was 5.2 s: the file used to start on the keyframe, 0.5 s before A).
        Assert.InRange(s["video"].End, 4.55, 4.85);
        Assert.InRange(s["audio"].End, 4.55, 4.85);
    }

    private static string Q(double v) => v.ToString("0.######", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>The source's frames as (pts, md5) on the tfdt axis: the chunks of video, byte-concatenated, decoded with -copyts.</summary>
    private async Task<List<(double Pts, string Md5)>> SourceFramesAsync()
    {
        var joined = Path.Combine(_out, "source.mp4");
        Directory.CreateDirectory(_out);
        await using (var target = File.Create(joined))
            foreach (var name in new[] { "init-stream0.m4s" }.Concat(Enumerable.Range(1, 4).Select(c => FolderNames.ChunkFile(0, c))))
                await target.WriteAsync(await File.ReadAllBytesAsync(Path.Combine(_clip, name)));
        return await FramesAsync(joined, copyts: true);
    }

    private static async Task<List<(double Pts, string Md5)>> FramesAsync(string file, bool copyts = false, bool ignoreEditList = false, int? count = null)
    {
        var args = new List<string> { "-v", "error" };
        if (copyts) args.Add("-copyts");
        if (ignoreEditList) args.AddRange(["-ignore_editlist", "1"]);
        args.AddRange(["-i", file, "-map", "0:v:0", "-fps_mode", "passthrough"]);
        if (count is { } n) args.AddRange(["-frames:v", Q(n)]);
        args.AddRange(["-f", "framemd5", "-"]);
        var r = await Ff.RunAsync(Ff.Ffmpeg, args);
        var inv = System.Globalization.CultureInfo.InvariantCulture;
        var tb = double.Parse(r.StdOut.Split('\n').First(l => l.StartsWith("#tb", StringComparison.Ordinal)).Split("1/")[1], inv);
        return [.. r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l[0] != '#')
            .Select(l => l.Split(',', StringSplitOptions.TrimEntries)).Select(p => (double.Parse(p[2], inv) / tb, p[5]))];
    }

    private async Task<double> ShownFrameAsync(string file, bool ignoreEditList = false, List<(double Pts, string Md5)>? source = null)
    {
        source ??= await SourceFramesAsync();
        var first = (await FramesAsync(file, ignoreEditList: ignoreEditList, count: 1))[0];
        return source.Single(f => f.Md5 == first.Md5).Pts;           // the source time of the frame the player shows first
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.8)]
    [InlineData(1.8)]
    public async Task Lossless_save_clip_plays_from_a_not_from_the_keyframe(double delay)
    {
        const double a = O + 4.4, b = O + 9.2;                       // the keyframe is at O + 3: 1.4 s of hidden lead-in
        var r = await ExportService.RunAsync(Job(Copy, new ExportSpan(a, b)) with { AudioDelay = delay }, null, default);
        var info = await Ff.ProbeAsync(r.Path);
        Assert.InRange(info!.Duration, 4.8 - 0.05, 4.8 + 0.1);        // B - A, not B - keyframe (6.2)
        var source = await SourceFramesAsync();
        var firstShown = await ShownFrameAsync(r.Path, source: source);
        Assert.InRange(firstShown, a - 0.001, a + 0.034);             // the frame at A: the first one with a time >= A
        Assert.Equal(O + 3, await ShownFrameAsync(r.Path, ignoreEditList: true, source: source), 2);   // a player that ignores edit lists starts on the keyframe
        var s = await StreamsAsync(r.Path);
        Assert.InRange(s["video"].Start, 0, 0.05);
        Assert.InRange(s["audio"].Start, 0, 0.05);                    // sound starts with the picture: no silent or frozen start
        Assert.InRange(s["audio"].End, 4.8 - 0.05, 4.8 + 0.05);       // both streams end on B
        Assert.InRange(s["video"].End, 4.8 - 0.05, 4.8 + 0.1);
    }

    [Fact]
    public async Task Lossless_from_a_keyframe_has_no_hidden_lead_in()
    {
        var r = await ExportService.RunAsync(Job(Copy, new ExportSpan(O + 3, O + 8)), null, default);
        Assert.InRange((await Ff.ProbeAsync(r.Path))!.Duration, 4.95, 5.1);
        var source = await SourceFramesAsync();
        Assert.Equal(await ShownFrameAsync(r.Path, source: source), await ShownFrameAsync(r.Path, ignoreEditList: true, source: source), 3);
    }

    [Fact]
    public async Task Lossless_markers_are_chapters_counted_from_a_and_the_edit_list_survives()
    {
        const double a = O + 4.4, b = O + 9.2;
        var job = Job(Copy, new ExportSpan(a, b)) with { Chapters = [new ExportChapter(a + 1, "Kill"), new ExportChapter(O + 3.5, "before A")] };
        var r = await ExportService.RunAsync(job, null, default);
        var p = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-show_entries", "chapter=start_time:chapter_tags=title", "-of", "csv=p=0", r.Path]);
        Assert.Equal(["0.000000,Start", "1.000000,Kill"], p.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
        Assert.InRange((await Ff.ProbeAsync(r.Path))!.Duration, 4.75, 4.95);
        Assert.InRange(await ShownFrameAsync(r.Path), a - 0.001, a + 0.034);
    }

    [Fact]
    public async Task Two_lossless_spans_hide_the_lead_in_of_the_first_and_start_the_second_on_its_keyframe()
    {
        // The concat join can't carry an edit list into the middle of the file, so only the first span is frame-exact.
        var r = await ExportService.RunAsync(Job(Copy, new ExportSpan(O + 1, O + 2), new ExportSpan(O + 7.5, O + 10)), null, default);
        Assert.InRange((await Ff.ProbeAsync(r.Path))!.Duration, 5 - 0.1, 5 + 0.15);   // 1 s + (10 - 6) s
        var source = await SourceFramesAsync();
        Assert.InRange(await ShownFrameAsync(r.Path, source: source), O + 1 - 0.001, O + 1 + 0.034);
        var all = await FramesAsync(r.Path);
        var secondStart = source.Single(f => f.Md5 == all[30].Md5).Pts;    // 1 s into the output
        Assert.InRange(secondStart, O + 6 - 0.001, O + 6 + 0.07);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1.8)]                  // the tail ran out: only the chunks of the picture were read
    [InlineData(1.8)]
    public async Task A_cut_inside_the_recording_keeps_all_its_sound_under_any_sync(double delay)
    {
        var job = Job(X264, new ExportSpan(O + 4, O + 8.9)) with { AudioDelay = delay };
        var s = await StreamsAsync((await ExportService.RunAsync(job, null, default)).Path);
        Assert.InRange(s["video"].End - s["video"].Start, 4.75, 5.05);
        Assert.InRange(s["audio"].Start, 0, 0.1);
        Assert.InRange(s["audio"].End, 4.75, 5.05);
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
