// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Export;

/// <summary>A kept range on the media (tfdt) axis.</summary>
public readonly record struct ExportSpan(double Start, double End)
{
    public double Length => End - Start;
}

/// <summary>A named instant on the media axis; becomes a chapter when it falls inside the exported spans.</summary>
public sealed record ExportChapter(double MediaSeconds, string Title);

/// <summary>A chapter on the output file's axis.</summary>
public sealed record OutputChapter(double Start, double End, string Title);

/// <summary>Pure rules for the kept-span list.</summary>
public static class ExportSpans
{
    /// <summary>Sorted, without empty or invalid spans, overlapping and touching spans merged.</summary>
    public static IReadOnlyList<ExportSpan> Normalize(IEnumerable<ExportSpan> spans)
    {
        var result = new List<ExportSpan>();
        foreach (var s in spans.Where(s => double.IsFinite(s.Start) && double.IsFinite(s.End) && s.End > s.Start).OrderBy(s => s.Start))
        {
            if (result.Count > 0 && s.Start <= result[^1].End)
                result[^1] = result[^1] with { End = Math.Max(result[^1].End, s.End) };
            else
                result.Add(s);
        }
        return result;
    }

    /// <summary>
    /// One plan per output segment, in order. Lossless spans are read from the keyframe before their start and keep their exact end,
    /// then merge when they overlap, so a frame is never written twice. The FIRST lossless plan keeps its exact start: its lead-in
    /// frames go into the file but an MP4 edit list hides them, so the file plays from A. Later plans start on their keyframe and show
    /// it: the join (concat) can't carry an edit list into the middle of a file.
    /// </summary>
    public static IReadOnlyList<ExportPlan> Plans(SessionMedia media, IEnumerable<ExportSpan> spans, bool lossless)
    {
        var plans = Normalize(spans).Select(s => ExportPlan.For(media, s.Start, s.End)).OfType<ExportPlan>().ToList();
        if (!lossless) return plans;
        var snapped = Normalize(plans.Select(p => new ExportSpan(p.SnappedStart, p.End)));
        var result = snapped.Select(s => ExportPlan.For(media, s.Start, s.End)).OfType<ExportPlan>().ToList();
        if (result.Count > 0 && plans.Count > 0 && ExportPlan.For(media, plans[0].Start, result[0].End) is { } first
            && first.FirstChunk == result[0].FirstChunk)
            result[0] = first;
        return result;
    }

    /// <summary>
    /// ±<paramref name="before"/>/<paramref name="after"/> seconds around each moment, clamped to the recording and merged: the
    /// spans of a highlight reel.
    /// </summary>
    public static IReadOnlyList<ExportSpan> Highlights(SessionMedia media, IEnumerable<double> moments, double before = 5, double after = 3)
    {
        if (ExportPlan.Whole(media) is not { } whole) return [];
        return Normalize(moments.Where(double.IsFinite)
            .Where(m => m >= whole.Start && m <= whole.End)
            .Select(m => new ExportSpan(Math.Max(whole.Start, m - before), Math.Min(whole.End, m + after))));
    }

    /// <summary>Output length of the plans in seconds, at <paramref name="speed"/>.</summary>
    public static double OutputSeconds(IEnumerable<ExportPlan> plans, double speed) =>
        plans.Sum(p => p.End - p.Start) / Math.Max(0.01, speed);
}

/// <summary>Chapters on the output axis and their ffmetadata file.</summary>
public static class ExportChapters
{
    /// <summary>
    /// Keeps the chapters inside a plan, maps them through the cuts and the speed, and ends each where the next starts (the last at the
    /// output's end). Two markers on the same instant keep the first.
    /// </summary>
    public static IReadOnlyList<OutputChapter> Layout(IReadOnlyList<ExportPlan> plans, IEnumerable<ExportChapter> chapters, double speed)
    {
        var rate = Math.Max(0.01, speed);
        var total = ExportSpans.OutputSeconds(plans, rate);
        var placed = new List<(double At, string Title)>();
        foreach (var c in chapters.OrderBy(c => c.MediaSeconds))
        {
            double before = 0;
            foreach (var p in plans)
            {
                if (c.MediaSeconds >= p.Start && c.MediaSeconds < p.End)
                {
                    var at = (before + c.MediaSeconds - p.Start) / rate;
                    if (placed.Count == 0 || at - placed[^1].At >= 0.001) placed.Add((at, c.Title));
                    break;
                }
                before += p.End - p.Start;
            }
        }
        // MP4's chapter track has no room before its first chapter, so a first marker at 1 s would be moved to 0.
        if (placed.Count > 0 && placed[0].At >= 0.001) placed.Insert(0, (0, "Start"));
        var result = new List<OutputChapter>(placed.Count);
        for (var i = 0; i < placed.Count; i++)
            result.Add(new OutputChapter(placed[i].At, i + 1 < placed.Count ? placed[i + 1].At : total, placed[i].Title));
        return result;
    }

    /// <summary>The ffmetadata text (time base 1/1000), with <c>= ; # \</c> and newlines escaped as ffmpeg requires.</summary>
    public static string Metadata(IEnumerable<OutputChapter> chapters)
    {
        var sb = new StringBuilder(";FFMETADATA1\n");
        foreach (var c in chapters)
        {
            sb.Append("[CHAPTER]\nTIMEBASE=1/1000\n");
            sb.Append(CultureInfo.InvariantCulture, $"START={(long)Math.Round(c.Start * 1000)}\n");
            sb.Append(CultureInfo.InvariantCulture, $"END={(long)Math.Round(c.End * 1000)}\n");
            sb.Append("title=").Append(Escape(c.Title)).Append('\n');
        }
        return sb.ToString();
    }

    private static string Escape(string text)
    {
        var sb = new StringBuilder(text.Length);
        foreach (var ch in text)
        {
            if (ch is '=' or ';' or '#' or '\\' or '\n') sb.Append('\\');
            if (ch != '\r') sb.Append(ch);
        }
        return sb.ToString();
    }
}

/// <summary>What a sync detection means for the user (plan §5, Phase 3).</summary>
public enum SyncVerdict { NotApplied, InSync, Fix }

/// <param name="AudioDelay">The fix to apply, in seconds; 0 unless <see cref="Verdict"/> is <see cref="SyncVerdict.Fix"/>.</param>
public sealed record SyncAdvice(SyncVerdict Verdict, double AudioDelay, double? AudioLate)
{
    /// <summary>
    /// The one helper that turns a detection into a fix: <c>AudioDelay = −AudioLate</c>, only with high confidence. A low-confidence
    /// result never yields a number: real gameplay usually gives one, and guessing would make good sync worse.
    /// </summary>
    public static SyncAdvice From(AudioSync.Result result)
    {
        if (result.Confidence != AudioSync.Confidence.High) return new(SyncVerdict.NotApplied, 0, null);
        return AudioSync.InSync(result.AudioLate)
            ? new(SyncVerdict.InSync, 0, result.AudioLate)
            : new(SyncVerdict.Fix, -result.AudioLate, result.AudioLate);
    }
}
