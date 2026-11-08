// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Export;

/// <summary>One export: a range of a recording, FastEncoder settings, and where the file goes.</summary>
/// <param name="ForbiddenRoot">Steam's recording folder: refused as a destination.</param>
public sealed record ExportJob(SessionMedia Media, double Start, double End, EncodeSettings Settings, EditSettings Edit,
    string OutputFolder, string Stem, string? ForbiddenRoot = null);

public sealed record ExportProgress(double Fraction, string Stage);

public sealed record ExportResult(string Path, long Bytes, ExportPlan Plan);

/// <summary>
/// Runs an export: copies the chunks through <see cref="RangeSource"/> first, so Steam can keep trimming its cache while ffmpeg works
/// on private bytes; muxes them on the real timestamps; then either that mux is the lossless result, or FastEncoder's
/// <see cref="Pipeline"/> encodes it with a frame-exact trim. Working files stay in a private folder next to the destination (same
/// volume, so the final move is atomic), so a failed or cancelled export never leaves a partial output.
/// </summary>
public static class ExportService
{
    private const string WorkPrefix = ".spp-export-";

    public static async Task<ExportResult> RunAsync(ExportJob job, IProgress<ExportProgress>? progress, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!Ff.Available) Ff.Locate();
        if (!Ff.Available) throw new InvalidOperationException("ffmpeg and ffprobe were not found on PATH.");
        if (ExportPaths.IsInside(job.OutputFolder, job.ForbiddenRoot))
            throw new InvalidOperationException("Exports can't be saved inside Steam's recording folder. Choose another folder.");
        var plan = ExportPlan.For(job.Media, job.Start, job.End)
            ?? throw new InvalidOperationException("Nothing to export: Steam no longer has this range.");

        Directory.CreateDirectory(job.OutputFolder);
        var work = Path.Combine(job.OutputFolder, WorkPrefix + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(work);
        File.SetAttributes(work, File.GetAttributes(work) | FileAttributes.Hidden);
        try
        {
            var lossless = job.Settings.Encoder == "copy";
            var (gatherEnd, muxEnd) = lossless ? (0.9, 1.0) : (0.05, 0.07);

            // The real first tfdt, read before Steam can trim it: the mux below starts the output at this instant.
            var origin = await ScanTfdt.ReadSecondsAsync(job.Media.Folder, plan.FirstChunk, ct).ConfigureAwait(false) ?? plan.SnappedStart;

            var video = Path.Combine(work, "video.mp4");
            var audio = Path.Combine(work, "audio.m4a");
            await GatherAsync(job.Media, plan, video, audio,
                f => progress?.Report(new ExportProgress(f * gatherEnd, "Reading chunks")), ct).ConfigureAwait(false);

            var mux = Path.Combine(work, "mux.mp4");
            await Pipeline.RunAsync(MuxArgs(video, audio, mux), plan.SnappedEnd - plan.SnappedStart,
                (f, _) => progress?.Report(new ExportProgress(gatherEnd + f * (muxEnd - gatherEnd), "Joining")), ct).ConfigureAwait(false);

            string produced;
            if (lossless)
            {
                produced = mux;
            }
            else
            {
                var info = await Ff.ProbeAsync(mux, ct).ConfigureAwait(false)
                    ?? throw new InvalidOperationException("ffprobe could not read the joined recording.");
                produced = Path.Combine(work, "encoded." + job.Settings.Container);
                var (trimStart, trimEnd) = OutputTrim(plan, origin, info.Duration);
                await Pipeline.EncodeAsync(new EncodeRequest(info, trimStart, trimEnd, job.Settings, job.Edit, produced),
                    (f, _, stage) => progress?.Report(new ExportProgress(muxEnd + f * (1 - muxEnd), stage + "Encoding")), ct).ConfigureAwait(false);
            }

            ct.ThrowIfCancellationRequested();
            var final = ExportPaths.Unique(job.OutputFolder, job.Stem, Path.GetExtension(produced));
            File.Move(produced, final);
            progress?.Report(new ExportProgress(1, "Done"));
            return new ExportResult(final, new FileInfo(final).Length, plan);
        }
        finally
        {
            try { Directory.Delete(work, recursive: true); }                // only ever this job's own working folder, never a recording
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
        }
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

    /// <summary>"Steam already removed …" text for a chunk that vanished during an export.</summary>
    public static string DescribeGone(SessionMedia media, ChunkGoneException gone) =>
        gone.Chunk is { } c
            ? string.Create(CultureInfo.InvariantCulture, $"Steam removed the part at {Pipeline.Time(Math.Max(0, media.ChunkStartSeconds(c) - media.TfdtOriginSeconds))} while it was being read. Export a later range, or save a clip in Steam first.")
            : "Steam removed this recording while it was being read.";
}
