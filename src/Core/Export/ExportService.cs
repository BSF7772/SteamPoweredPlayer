// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Export;

/// <summary>One export: kept spans of a recording, FastEncoder settings and edits, and where the file goes.</summary>
/// <param name="ForbiddenRoot">Steam's recording folder: refused as a destination.</param>
/// <param name="AudioDelay">Seconds to delay the soundtrack against the picture (negative = play it earlier).</param>
/// <param name="Chapters">Markers to write as chapters; those outside the spans are dropped.</param>
public sealed record ExportJob(SessionMedia Media, IReadOnlyList<ExportSpan> Spans, EncodeSettings Settings, EditSettings Edit,
    string OutputFolder, string Stem, string? ForbiddenRoot = null, double AudioDelay = 0, IReadOnlyList<ExportChapter>? Chapters = null)
{
    public bool Lossless => Settings.Encoder == "copy";
}

public sealed record ExportProgress(double Fraction, string Stage);

public sealed record ExportResult(string Path, long Bytes, IReadOnlyList<ExportPlan> Plans);

/// <summary>
/// Runs an export. Each kept span is handled alone: its chunks are copied through <see cref="RangeSource"/> first, so Steam can keep
/// trimming its cache while ffmpeg works on private bytes. The audio takes extra chunks (<see cref="ExportPlan.AudioChunks"/>), so a
/// sync shift or Steam's jitter never runs out of sound. A lossless export stores the kept ranges' whole chunks and shows exactly their
/// frames through edit lists (<see cref="LosslessEdits"/>); a re-encode goes through FastEncoder's <see cref="Pipeline"/> with a frame-exact
/// trim, and several spans are joined without re-encoding again.
/// Working files stay in a private folder next to the destination (same volume, so the final move is atomic), so a failed or
/// cancelled export never leaves a partial output.
/// </summary>
public static class ExportService
{
    private const string WorkPrefix = ".spp-export-";
    private const double SampleSeconds = 6;
    private const double DetectSeconds = 75;   // AudioSync analyses at most this much, from the middle

    private static void RequireFfmpeg()
    {
        if (!Ff.Available) Ff.Locate();
        if (!Ff.Available) throw new InvalidOperationException("ffmpeg and ffprobe were not found on PATH.");
    }

    public static async Task<ExportResult> RunAsync(ExportJob job, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        RequireFfmpeg();
        if (ExportPaths.IsInside(job.OutputFolder, job.ForbiddenRoot))
            throw new InvalidOperationException("Exports can't be saved inside Steam's recording folder. Choose another folder.");
        var plans = ExportSpans.Plans(job.Media, job.Spans);
        if (plans.Count == 0) throw new InvalidOperationException("Nothing to export: Steam no longer has this range.");

        Directory.CreateDirectory(job.OutputFolder);
        var work = Path.Combine(job.OutputFolder, WorkPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        File.SetAttributes(work, File.GetAttributes(work) | FileAttributes.Hidden);
        try
        {
            if (job.Lossless)
            {
                var clip = await LosslessAsync(job, plans, work, (f, stage) => progress?.Report(new ExportProgress(Math.Min(0.99, f), stage)), ct).ConfigureAwait(false);
                ct.ThrowIfCancellationRequested();
                var saved = ExportPaths.Unique(job.OutputFolder, job.Stem, ".mp4");
                File.Move(clip, saved);
                progress?.Report(new ExportProgress(1, "Done"));
                return new ExportResult(saved, new FileInfo(saved).Length, plans);
            }

            var extension = "." + job.Settings.Container;
            var totalSpan = plans.Sum(p => p.End - p.Start);
            var segments = new List<string>(plans.Count);
            double finished = 0;
            // One frame rate for all parts. Each part measured alone can pick another (59.94 and 60 on one real recording), and a join across
            // their different time bases stamps every later frame on one instant.
            var sharedFps = plans.Count > 1 ? ChunkFrameRate.Measure(job.Media) : null;
            for (var i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                var share = (plan.End - plan.Start) / totalSpan;
                var from = finished;
                var label = plans.Count > 1 ? $"Part {i + 1}/{plans.Count} · " : "";
                segments.Add(await SegmentAsync(job, plan, totalSpan, sharedFps, Path.Combine(work, $"s{i}"), extension,
                    (f, stage) => progress?.Report(new ExportProgress(Math.Min(0.99, from + f * share), label + stage)), ct).ConfigureAwait(false));
                finished += share;
            }

            var produced = segments[0];
            var outputSeconds = ExportSpans.OutputSeconds(plans, job.Edit.Speed);
            if (segments.Count > 1)
            {
                produced = Path.Combine(work, "joined" + extension);
                var list = Path.Combine(work, "list.txt");
                await File.WriteAllTextAsync(list, string.Concat(segments.Select(s => $"file '{Path.GetFileName(Path.GetDirectoryName(s))}/{Path.GetFileName(s)}'\n")), ct).ConfigureAwait(false);
                await Pipeline.RunAsync(ConcatArgs(list, produced), outputSeconds, (_, _) => { }, ct).ConfigureAwait(false);
                await RequireWholePictureAsync(produced, outputSeconds, ct).ConfigureAwait(false);
            }

            var chapters = ExportChapters.Layout(plans, job.Chapters ?? [], job.Edit.Speed);
            if (chapters.Count > 0)
            {
                var meta = Path.Combine(work, "chapters.txt");
                await File.WriteAllTextAsync(meta, ExportChapters.Metadata(chapters), ct).ConfigureAwait(false);
                var titled = Path.Combine(work, "titled" + extension);
                await Pipeline.RunAsync(ChapterArgs(produced, meta, titled), outputSeconds, (_, _) => { }, ct).ConfigureAwait(false);
                produced = titled;
            }

            ct.ThrowIfCancellationRequested();
            var final = ExportPaths.Unique(job.OutputFolder, job.Stem, extension);
            File.Move(produced, final);
            progress?.Report(new ExportProgress(1, "Done"));
            return new ExportResult(final, new FileInfo(final).Length, plans);
        }
        finally
        {
            RemoveWork(work);
        }
    }

    /// <summary>A join whose picture stops well short is an error to show, not a file to save: its sound would still run to the end, so nothing else would tell.</summary>
    private static async Task RequireWholePictureAsync(string joined, double expected, CancellationToken ct)
    {
        var frames = await ProbePacketsAsync(joined, "v:0", [], ct).ConfigureAwait(false);
        if (!IsWholePicture(frames.Select(f => f.Seconds), expected))
            throw new InvalidOperationException("Joining the parts failed: the picture came out shorter than the kept range. Nothing was saved.");
    }

    /// <summary>True when the frames reach the expected length, within half a second or 5 %, whichever is more.</summary>
    public static bool IsWholePicture(IEnumerable<double> frameTimes, double expected)
    {
        var last = frameTimes.DefaultIfEmpty(0).Max();
        return last >= expected - Math.Max(0.5, expected * 0.05);
    }

    private static async Task<string> SegmentAsync(ExportJob job, ExportPlan plan, double totalSpan, double? sharedFps, string dir, string extension,
        Action<double, string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var audio = ExportPlan.AudioChunks(job.Media, plan, job.AudioDelay);

        const double gatherEnd = 0.05, muxEnd = 0.07;
        var (mux, origin) = await JoinAsync(job.Media, plan, audio, dir, gatherEnd, muxEnd, progress, ct).ConfigureAwait(false);
        var info = await Ff.ProbeAsync(mux, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("ffprobe could not read the joined recording.");
        if (sharedFps is { } fps) info.Fps = fps;
        var output = Path.Combine(dir, "out" + extension);
        var (start, end) = OutputTrim(plan, origin, info.Duration);
        var settings = job.Settings.Clone();
        if (settings.TargetSize) settings.TargetMB *= (plan.End - plan.Start) / totalSpan;          // the budget is for the whole file
        await Pipeline.EncodeAsync(new EncodeRequest(info, start, end, settings, job.Edit, output, AudioDelay: job.AudioDelay),
            (f, _, stage) => progress(muxEnd + f * (1 - muxEnd), stage + "Encoding"), ct).ConfigureAwait(false);
        File.Delete(mux);                                                                            // this job's own file: free the space early
        return output;
    }

    /// <summary>
    /// A lossless export. The picture only starts on a chunk's first frame, and with B-frames a frame shown before B can need one stored
    /// after it, so every kept range's whole chunks go into one file, each chunk once, on their real timestamps. Edit lists then show
    /// exactly the source frames in each range and hide the rest: no sample is touched. Chapters go in with the join, because a later
    /// remux would read through the edit lists.
    /// </summary>
    private static async Task<string> LosslessAsync(ExportJob job, IReadOnlyList<ExportPlan> plans, string dir, Action<double, string> progress, CancellationToken ct)
    {
        const double gatherEnd = 0.85, muxEnd = 0.95;
        var video = Path.Combine(dir, "video.mp4");
        var audio = Path.Combine(dir, "audio.m4a");
        var videoChunks = plans.SelectMany(p => Enumerable.Range(p.FirstChunk, p.LastChunk - p.FirstChunk + 1)).ToHashSet();
        var audioChunks = plans.Select(p => ExportPlan.AudioChunks(job.Media, p, job.AudioDelay)).SelectMany(r => Enumerable.Range(r.First, r.Last - r.First + 1)).ToHashSet();
        await using (var v = RangeSource.Create(job.Media, 0, videoChunks))
        await using (var a = RangeSource.Create(job.Media, 1, audioChunks))
            await GatherAsync(v, a, video, audio, f => progress(f * gatherEnd, "Reading chunks"), ct).ConfigureAwait(false);

        var chapters = ExportChapters.Layout(plans, job.Chapters ?? [], 1);
        string? metadata = null;
        if (chapters.Count > 0)
        {
            metadata = Path.Combine(dir, "chapters.txt");
            await File.WriteAllTextAsync(metadata, ExportChapters.Metadata(chapters), ct).ConfigureAwait(false);
        }
        var mux = Path.Combine(dir, "mux.mp4");
        await Pipeline.RunAsync(MuxArgs(video, audio, mux, metadata), plans.Sum(p => p.SnappedEnd - p.SnappedStart),
            (f, _) => progress(gatherEnd + f * (muxEnd - gatherEnd), "Joining"), ct).ConfigureAwait(false);

        var scales = Mp4EditList.ReadTimescales(mux);
        var stored = await Task.WhenAll(PacketsAsync(video, mux, "v:0", ct), PacketsAsync(audio, mux, "a:0", ct)).ConfigureAwait(false);
        var ranges = plans.Select(p => RangeOf(job.Media, p)).ToList();
        var (pictureEdits, soundEdits) = LosslessEdits.Compute(ranges, stored[0], stored[1], scales, job.AudioDelay);
        if (pictureEdits.Count == 0) throw new InvalidOperationException("Nothing to export: no frame falls inside this range.");

        var output = Path.Combine(dir, "out.mp4");
        Mp4EditList.Rewrite(mux, output, pictureEdits, soundEdits);
        File.Delete(video);                                                                          // this job's own files: free the space early
        File.Delete(audio);
        File.Delete(mux);
        return output;
    }

    /// <summary>A range's frames are those in [Start, End), except at the recording's own first and last chunk: no frame there is ever cut for sitting a few ms off the grid.</summary>
    public static LosslessRange RangeOf(SessionMedia media, ExportPlan plan) =>
        new(plan.Start, plan.End,
            plan.FirstChunk == media.FirstChunk && plan.Start <= plan.SnappedStart + 0.001,
            plan.LastChunk == media.LastChunk && plan.End >= plan.SnappedEnd - 0.001);

    /// <summary>A track's packets on the source axis, paired in file order with their ticks on the joined file's media axis (edit lists ignored).</summary>
    private static async Task<StoredPackets> PacketsAsync(string source, string joined, string stream, CancellationToken ct)
    {
        var from = await ProbePacketsAsync(source, stream, [], ct).ConfigureAwait(false);
        var to = await ProbePacketsAsync(joined, stream, ["-ignore_editlist", "1"], ct).ConfigureAwait(false);
        return new StoredPackets(from.Select(p => p.Seconds).ToList(), to.Select(p => p.Ticks).ToList());
    }

    private static async Task<List<(long Ticks, double Seconds)>> ProbePacketsAsync(string path, string stream, string[] options, CancellationToken ct)
    {
        var r = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", .. options, "-select_streams", stream, "-show_entries", "packet=pts,pts_time", "-of", "csv=p=0", path], ct).ConfigureAwait(false);
        var result = new List<(long, double)>();
        foreach (var line in r.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries))
        {
            var cells = line.Split(',');
            if (cells.Length < 2 || !long.TryParse(cells[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var ticks)
                || !double.TryParse(cells[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var seconds))
                throw new InvalidOperationException("ffprobe gave a packet without a time in the joined recording.");
            result.Add((ticks, seconds));
        }
        return result;
    }

    /// <summary>
    /// The first packet's dts as ffmpeg reads it. A chunk's tfdt isn't that: the init segment's edit list moves it, by 66 ms on a real clip.
    /// </summary>
    private static async Task<double?> FirstDtsAsync(string path, CancellationToken ct)
    {
        var r = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-show_entries", "packet=dts_time", "-read_intervals", "%+#1", "-of", "csv=p=0", path], ct).ConfigureAwait(false);
        return double.TryParse(r.StdOut.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries).FirstOrDefault()?.TrimEnd(','), NumberStyles.Float, CultureInfo.InvariantCulture, out var dts) ? dts : null;
    }

    /// <summary>Copies a plan's chunks into <paramref name="dir"/> and muxes them; returns the mux and the instant that became its 0.</summary>
    private static async Task<(string Mux, double Origin)> JoinAsync(SessionMedia media, ExportPlan plan, (int First, int Last) audioChunks, string dir, double gatherEnd, double muxEnd,
        Action<double, string> progress, CancellationToken ct)
    {
        var video = Path.Combine(dir, "video.mp4");
        var audio = Path.Combine(dir, "audio.m4a");
        await using (var v = RangeSource.Create(media, 0, plan.FirstChunk, plan.LastChunk))
        await using (var a = RangeSource.Create(media, 1, audioChunks.First, audioChunks.Last))
            await GatherAsync(v, a, video, audio, f => progress(f * gatherEnd, "Reading chunks"), ct).ConfigureAwait(false);
        // make_zero below moves the earliest packet of the two to 0, so that instant is where the trim counts from.
        var origin = Math.Min(await FirstDtsAsync(video, ct).ConfigureAwait(false) ?? plan.SnappedStart, await FirstDtsAsync(audio, ct).ConfigureAwait(false) ?? double.MaxValue);
        var mux = Path.Combine(dir, "mux.mp4");
        await Pipeline.RunAsync(MuxArgs(video, audio, mux), plan.SnappedEnd - plan.SnappedStart,
            (f, _) => progress(gatherEnd + f * (muxEnd - gatherEnd), "Joining"), ct).ConfigureAwait(false);
        File.Delete(video);                                                                          // this job's own files: free the space early
        File.Delete(audio);
        return (mux, origin);
    }

    /// <summary>
    /// Trim on the joined file's axis, where <paramref name="origin"/> (the first chunk's real tfdt) is 0. The end is dropped when it
    /// reaches the end of the file, so jitter can't cut the last frames.
    /// </summary>
    public static (double Start, double? End) OutputTrim(ExportPlan plan, double origin, double duration)
    {
        var start = Math.Max(0, plan.Start - origin);
        var end = plan.End - origin;
        return (start < 0.001 ? 0 : start, end >= duration - 0.001 ? null : end);
    }

    /// <summary><c>-copyts</c> keeps the absolute tfdt of both streams, so they stay aligned; <c>make_zero</c> then moves them together to 0.</summary>
    public static List<string> MuxArgs(string video, string audio, string output, string? chapterMetadata = null) =>
    [
        "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-copyts",
        "-i", video, "-i", audio, .. chapterMetadata is null ? (string[])[] : ["-f", "ffmetadata", "-i", chapterMetadata, "-map_chapters", "2"],
        "-map", "0:v:0", "-map", "1:a:0", "-c", "copy",
        "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", output,
    ];

    private static List<string> ConcatArgs(string list, string output) =>
    [
        "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
        "-f", "concat", "-safe", "0", "-i", list, "-map", "0", "-c", "copy", "-movflags", "+faststart", output,
    ];

    private static List<string> ChapterArgs(string input, string metadata, string output) =>
    [
        "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats",
        "-i", input, "-f", "ffmetadata", "-i", metadata, "-map", "0", "-map_metadata", "0", "-map_chapters", "1", "-c", "copy",
        "-movflags", "+faststart", output,
    ];

    private static async Task GatherAsync(RangeSource v, RangeSource a, string video, string audio, Action<double> progress, CancellationToken ct)
    {
        var total = Math.Max(1, v.Length + a.Length);
        long done = 0;
        foreach (var (source, path) in new[] { ((Stream)v, video), (a, audio) })
        {
            await using var target = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None, 1 << 16, useAsync: true);
            var buffer = new byte[1 << 20];
            int n;
            while ((n = await source.ReadAsync(buffer, ct).ConfigureAwait(false)) > 0)
            {
                await target.WriteAsync(buffer.AsMemory(0, n), ct).ConfigureAwait(false);
                done += n;
                progress(done / (double)total);
            }
        }
    }

    // ---- sync detection -------------------------------------------------------------------------------------------------

    /// <summary>
    /// Runs FastEncoder's detector on the middle of <paramref name="span"/> (it analyses at most 75 s, so only those chunks are read).
    /// Works in a private temp folder.
    /// </summary>
    public static async Task<AudioSync.Result> DetectSyncAsync(SessionMedia media, ExportSpan span, Action<double>? progress, CancellationToken ct)
    {
        RequireFfmpeg();
        var mid = (span.Start + span.End) / 2;
        var window = span.Length <= DetectSeconds ? span : new ExportSpan(mid - DetectSeconds / 2, mid + DetectSeconds / 2);
        var plan = ExportPlan.For(media, window.Start, window.End)
            ?? throw new InvalidOperationException("Nothing to analyse: Steam no longer has this range.");
        var work = NewTempWork();
        try
        {
            var (mux, _) = await JoinAsync(media, plan, (plan.FirstChunk, plan.LastChunk), work, 0.3, 0.35, (f, _) => progress?.Invoke(f), ct).ConfigureAwait(false);
            var info = await Ff.ProbeAsync(mux, ct).ConfigureAwait(false)
                ?? throw new InvalidOperationException("ffprobe could not read the joined recording.");
            return await AudioSync.DetectAsync(mux, 0, info.Duration, f => progress?.Invoke(0.35 + f * 0.65), ct).ConfigureAwait(false);
        }
        finally { RemoveWork(work); }
    }

    // ---- size estimate --------------------------------------------------------------------------------------------------

    /// <summary>
    /// The output size in bytes. Lossless: the size of the chunks, exact. Fit to size: the target. Otherwise it encodes up to two
    /// 6 s samples with the job's settings and extrapolates.
    /// </summary>
    public static async Task<long> EstimateAsync(ExportJob job, CancellationToken ct)
    {
        RequireFfmpeg();
        var plans = ExportSpans.Plans(job.Media, job.Spans);
        if (plans.Count == 0) return 0;
        if (job.Lossless) return ChunkBytes(job.Media, plans);
        if (job.Settings.TargetSize) return (long)(job.Settings.TargetMB * 1024 * 1024);

        var total = plans.Sum(p => p.End - p.Start);
        var work = NewTempWork();
        try
        {
            long bytes = 0;
            double seconds = 0;
            var samples = SamplePlans(job.Media, plans);
            for (var i = 0; i < samples.Count; i++)
            {
                var dir = Path.Combine(work, $"e{i}");
                Directory.CreateDirectory(dir);
                var (mux, origin) = await JoinAsync(job.Media, samples[i], (samples[i].FirstChunk, samples[i].LastChunk), dir, 0.5, 1, (_, _) => { }, ct).ConfigureAwait(false);
                var info = await Ff.ProbeAsync(mux, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("ffprobe could not read the joined recording.");
                var output = Path.Combine(dir, "out." + job.Settings.Container);
                var (start, end) = OutputTrim(samples[i], origin, info.Duration);
                await Pipeline.EncodeAsync(new EncodeRequest(info, start, end, job.Settings, job.Edit, output), (_, _, _) => { }, ct).ConfigureAwait(false);
                bytes += new FileInfo(output).Length;
                seconds += samples[i].End - samples[i].Start;
            }
            return seconds > 0 ? (long)(bytes / seconds * total) : 0;
        }
        finally { RemoveWork(work); }
    }

    /// <summary>Up to two sample ranges of at most 6 s, at a quarter and three quarters of the kept length.</summary>
    public static IReadOnlyList<ExportPlan> SamplePlans(SessionMedia media, IReadOnlyList<ExportPlan> plans)
    {
        var total = plans.Sum(p => p.End - p.Start);
        double[] targets = total <= SampleSeconds * 2 ? [total / 2] : [total * 0.25, total * 0.75];
        var result = new List<ExportPlan>(targets.Length);
        foreach (var target in targets)
        {
            var left = target;
            foreach (var p in plans)
            {
                var length = p.End - p.Start;
                if (left > length) { left -= length; continue; }
                var half = Math.Min(SampleSeconds, length) / 2;
                var mid = Math.Clamp(p.Start + left, p.Start + half, p.End - half);
                if (ExportPlan.For(media, mid - half, mid + half) is { } sample) result.Add(sample);
                break;
            }
        }
        return result;
    }

    private static long ChunkBytes(SessionMedia media, IReadOnlyList<ExportPlan> plans)
    {
        long bytes = 0;
        foreach (var chunk in media.Chunks().Where(c => plans.Any(p => c >= p.FirstChunk && c <= p.LastChunk)))      // a chunk two ranges share is stored once
            for (var stream = 0; stream < 2; stream++)
            {
                var info = new FileInfo(Path.Combine(media.Folder, Steam.FolderNames.ChunkFile(stream, chunk)));
                if (info.Exists) bytes += info.Length;
            }
        return bytes;
    }

    // ---- working folders ------------------------------------------------------------------------------------------------

    private static string NewTempWork()
    {
        var work = Path.Combine(Path.GetTempPath(), "spp-export-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        return work;
    }

    private static void RemoveWork(string work)
    {
        try { Directory.Delete(work, recursive: true); }                    // only ever this job's own working folder, never a recording
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>
    /// Removes working folders left in <paramref name="folder"/> by an app that was killed mid-export. Only call while no export runs:
    /// the queue does. It matches the exact private prefix, so nothing else in the user's folder is touched.
    /// </summary>
    public static void RemoveStaleWork(string folder)
    {
        try
        {
            foreach (var dir in Directory.EnumerateDirectories(folder, WorkPrefix + "*", new EnumerationOptions { AttributesToSkip = 0 }))
                RemoveWork(dir);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }

    /// <summary>"Steam already removed …" text for a chunk that vanished during an export.</summary>
    public static string DescribeGone(SessionMedia media, ChunkGoneException gone) =>
        gone.Chunk is { } c
            ? string.Create(CultureInfo.InvariantCulture, $"Steam removed the part at {Pipeline.Time(Math.Max(0, media.ChunkStartSeconds(c) - media.TfdtOriginSeconds))} while it was being read. Export a later range, or save a clip in Steam first.")
            : "Steam removed this recording while it was being read.";
}
