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
/// trimming its cache while ffmpeg works on private bytes; they are muxed on the real timestamps; then that mux is the lossless
/// result, or FastEncoder's <see cref="Pipeline"/> encodes it with a frame-exact trim. Several spans are joined without re-encoding.
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
        var plans = ExportSpans.Plans(job.Media, job.Spans, job.Lossless);
        if (plans.Count == 0) throw new InvalidOperationException("Nothing to export: Steam no longer has this range.");

        Directory.CreateDirectory(job.OutputFolder);
        var work = Path.Combine(job.OutputFolder, WorkPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        File.SetAttributes(work, File.GetAttributes(work) | FileAttributes.Hidden);
        try
        {
            var extension = job.Lossless ? ".mp4" : "." + job.Settings.Container;
            var totalSpan = plans.Sum(p => p.End - p.Start);
            var segments = new List<string>(plans.Count);
            double finished = 0;
            for (var i = 0; i < plans.Count; i++)
            {
                var plan = plans[i];
                var share = (plan.End - plan.Start) / totalSpan;
                var from = finished;
                var label = plans.Count > 1 ? $"Part {i + 1}/{plans.Count} · " : "";
                segments.Add(await SegmentAsync(job, plan, totalSpan, Path.Combine(work, $"s{i}"), extension,
                    (f, stage) => progress?.Report(new ExportProgress(Math.Min(0.99, from + f * share), label + stage)), ct).ConfigureAwait(false));
                finished += share;
            }

            var produced = segments[0];
            var outputSeconds = ExportSpans.OutputSeconds(plans, job.Lossless ? 1 : job.Edit.Speed);
            if (segments.Count > 1)
            {
                produced = Path.Combine(work, "joined" + extension);
                var list = Path.Combine(work, "list.txt");
                await File.WriteAllTextAsync(list, string.Concat(segments.Select(s => $"file '{Path.GetFileName(Path.GetDirectoryName(s))}/{Path.GetFileName(s)}'\n")), ct).ConfigureAwait(false);
                await Pipeline.RunAsync(ConcatArgs(list, produced), outputSeconds, (_, _) => { }, ct).ConfigureAwait(false);
            }

            var chapters = ExportChapters.Layout(plans, job.Chapters ?? [], job.Lossless ? 1 : job.Edit.Speed);
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

    private static async Task<string> SegmentAsync(ExportJob job, ExportPlan plan, double totalSpan, string dir, string extension,
        Action<double, string> progress, CancellationToken ct)
    {
        Directory.CreateDirectory(dir);
        var (gatherEnd, muxEnd) = job.Lossless ? (0.9, 1.0) : (0.05, 0.07);
        var (mux, origin) = await JoinAsync(job.Media, plan, dir, gatherEnd, muxEnd, progress, ct).ConfigureAwait(false);
        if (job.Lossless && Math.Abs(job.AudioDelay) <= 0.0005) return mux;

        var info = await Ff.ProbeAsync(mux, ct).ConfigureAwait(false)
            ?? throw new InvalidOperationException("ffprobe could not read the joined recording.");
        var output = Path.Combine(dir, "out" + extension);
        var (start, end) = job.Lossless ? (0, null) : OutputTrim(plan, origin, info.Duration);
        var settings = job.Settings.Clone();
        if (settings.TargetSize) settings.TargetMB *= (plan.End - plan.Start) / totalSpan;          // the budget is for the whole file
        await Pipeline.EncodeAsync(new EncodeRequest(info, start, end, settings, job.Edit, output, AudioDelay: job.AudioDelay),
            (f, _, stage) => progress(muxEnd + f * (1 - muxEnd), stage + "Encoding"), ct).ConfigureAwait(false);
        File.Delete(mux);                                                                            // this job's own file: free the space early
        return output;
    }

    /// <summary>Copies a plan's chunks into <paramref name="dir"/> and muxes them; returns the mux and the first chunk's real tfdt.</summary>
    private static async Task<(string Mux, double Origin)> JoinAsync(SessionMedia media, ExportPlan plan, string dir, double gatherEnd, double muxEnd,
        Action<double, string> progress, CancellationToken ct)
    {
        // Read before Steam can trim it: the mux below starts the output at this instant.
        var origin = await ScanTfdt.ReadSecondsAsync(media.Folder, plan.FirstChunk, ct).ConfigureAwait(false) ?? plan.SnappedStart;
        var video = Path.Combine(dir, "video.mp4");
        var audio = Path.Combine(dir, "audio.m4a");
        await GatherAsync(media, plan, video, audio, f => progress(f * gatherEnd, "Reading chunks"), ct).ConfigureAwait(false);
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
    public static List<string> MuxArgs(string video, string audio, string output) =>
    [
        "-hide_banner", "-nostdin", "-y", "-loglevel", "error", "-progress", "pipe:1", "-nostats", "-copyts",
        "-i", video, "-i", audio, "-map", "0:v:0", "-map", "1:a:0", "-c", "copy",
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

    private static async Task GatherAsync(SessionMedia media, ExportPlan plan, string video, string audio, Action<double> progress, CancellationToken ct)
    {
        await using var v = RangeSource.Create(media, 0, plan.FirstChunk, plan.LastChunk);
        await using var a = RangeSource.Create(media, 1, plan.FirstChunk, plan.LastChunk);
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
            var (mux, _) = await JoinAsync(media, plan, work, 0.3, 0.35, (f, _) => progress?.Invoke(f), ct).ConfigureAwait(false);
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
        var plans = ExportSpans.Plans(job.Media, job.Spans, job.Lossless);
        if (plans.Count == 0) return 0;
        if (job.Lossless) return plans.Sum(p => ChunkBytes(job.Media, p));
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
                var (mux, origin) = await JoinAsync(job.Media, samples[i], dir, 0.5, 1, (_, _) => { }, ct).ConfigureAwait(false);
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

    private static long ChunkBytes(SessionMedia media, ExportPlan plan)
    {
        long bytes = 0;
        foreach (var chunk in media.Chunks().Where(c => c >= plan.FirstChunk && c <= plan.LastChunk))
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
