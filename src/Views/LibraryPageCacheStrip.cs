// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace SteamPoweredPlayer.Views;

/// <summary>A cache session as the strip draws it: the wall-clock span of the chunks on disk (UTC).</summary>
public readonly record struct StripSpan(DateTimeOffset StartUtc, DateTimeOffset EndUtc)
{
    public double Seconds => Math.Max(0, (EndUtc - StartUtc).TotalSeconds);
}

/// <summary>One drawn block (index into the input) from <see cref="X0"/> to <see cref="X1"/> pixels.</summary>
public readonly record struct StripBlock(int Index, double X0, double X1, StripSpan Span);

/// <summary>The space between two blocks. <see cref="Compressed"/>: drawn shorter than its real length (a break mark is shown).</summary>
public readonly record struct StripGap(double X0, double X1, TimeSpan Length, bool Compressed);

/// <summary>A label under the axis at <see cref="X"/> (local time; the date is included when the day changes).</summary>
public readonly record struct StripTick(double X, string Label, bool IsEdge);

public sealed record StripLayout(IReadOnlyList<StripBlock> Blocks, IReadOnlyList<StripGap> Gaps, IReadOnlyList<StripTick> Ticks)
{
    public static StripLayout Empty { get; } = new([], [], []);
}

/// <summary>
/// Lays out the Library page's cache strip (F9): every cache session is a block on an absolute time axis, scaled to the real span
/// (minutes to days). Long gaps between sessions are compressed to a short break so a session from yesterday does not squeeze
/// today's into a sliver; the gap stays visible. Pure (no XAML), so it is unit-tested.
/// </summary>
public static class CacheStripLayout
{
    /// <summary>Minimum width of a block, so a very short session stays clickable.</summary>
    public const double MinBlockWidth = 10;
    /// <summary>Minimum width of a gap between two blocks, so they never touch.</summary>
    public const double MinGapWidth = 6;
    /// <summary>A gap longer than this share of the recorded time (and 10 min) is drawn as a compressed break of <see cref="BreakWidth"/> px.</summary>
    public const double CompressShare = 1.0;
    public const double BreakWidth = 36;
    /// <summary>Labels closer than this (px) to the previous one are dropped.</summary>
    public const double MinLabelSpacing = 72;

    private static readonly double[] TickSteps = [60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800];

    public static StripLayout Build(IReadOnlyList<StripSpan> spans, double width, TimeZoneInfo zone)
    {
        if (spans.Count == 0 || !(width > 2 * MinBlockWidth)) return StripLayout.Empty;
        var order = Enumerable.Range(0, spans.Count).OrderBy(i => spans[i].StartUtc).ToArray();

        // Piecewise axis: blocks keep their real length, a long gap shrinks to a fixed break.
        var recorded = order.Sum(i => Math.Max(spans[i].Seconds, 1));
        var compressAbove = Math.Max(recorded * CompressShare, 600);
        var gaps = new List<(double Seconds, bool Compressed)>();
        for (var k = 1; k < order.Length; k++)
        {
            var g = Math.Max(0, (spans[order[k]].StartUtc - spans[order[k - 1]].EndUtc).TotalSeconds);
            gaps.Add((g, g > compressAbove));
        }
        var fixedPx = gaps.Sum(g => g.Compressed ? BreakWidth : MinGapWidth * (g.Seconds > 0 ? 1 : 0));
        var flexSeconds = recorded + gaps.Where(g => !g.Compressed).Sum(g => g.Seconds);
        var minBlocksPx = order.Length * MinBlockWidth;
        var pxPerSecond = Math.Max(0, width - fixedPx - minBlocksPx) / Math.Max(flexSeconds, 1);

        var blocks = new List<StripBlock>();
        var gapList = new List<StripGap>();
        var x = 0.0;
        for (var k = 0; k < order.Length; k++)
        {
            if (k > 0)
            {
                var (seconds, compressed) = gaps[k - 1];
                var gw = compressed ? BreakWidth : seconds > 0 ? MinGapWidth + seconds * pxPerSecond : 0;
                if (gw > 0) gapList.Add(new(x, x + gw, TimeSpan.FromSeconds(seconds), compressed));
                x += gw;
            }
            var s = spans[order[k]];
            var bw = MinBlockWidth + Math.Max(s.Seconds, 1) * pxPerSecond;
            blocks.Add(new(order[k], x, x + bw, s));
            x += bw;
        }
        // rounding: stretch the result exactly to the width
        var scale = x > 0 ? width / x : 1;
        if (Math.Abs(scale - 1) > 1e-9)
        {
            blocks = [.. blocks.Select(b => b with { X0 = b.X0 * scale, X1 = b.X1 * scale })];
            gapList = [.. gapList.Select(g => g with { X0 = g.X0 * scale, X1 = g.X1 * scale })];
        }

        return new(blocks, gapList, Ticks(blocks, zone));
    }

    /// <summary>Where wall-clock time <paramref name="utc"/> falls inside <paramref name="block"/> (clamped to it).</summary>
    public static double XOf(StripBlock block, DateTimeOffset utc)
    {
        var len = block.Span.Seconds;
        var f = len <= 0 ? 0 : Math.Clamp((utc - block.Span.StartUtc).TotalSeconds / len, 0, 1);
        return block.X0 + f * (block.X1 - block.X0);
    }

    private static long Minute(DateTimeOffset t) => t.ToUnixTimeSeconds() / 60;

    private static List<StripTick> Ticks(List<StripBlock> blocks, TimeZoneInfo zone)
    {
        // Candidates: each block's start and end (edges), plus round times inside blocks wide enough for them.
        var cand = new List<(double X, DateTimeOffset Utc, bool Edge)>();
        foreach (var b in blocks)
        {
            cand.Add((b.X0, b.Span.StartUtc, true));
            var px = b.X1 - b.X0;
            var len = b.Span.Seconds;
            if (len > 0 && px > 2 * MinLabelSpacing)
            {
                var want = len * MinLabelSpacing * 1.5 / px;            // seconds per label at a comfortable density
                var step = TickSteps.FirstOrDefault(t => t >= want, TickSteps[^1]);
                var local = TimeZoneInfo.ConvertTime(b.Span.StartUtc, zone);
                var midnight = new DateTimeOffset(local.Date, local.Offset);
                var first = Math.Ceiling((local - midnight).TotalSeconds / step) * step;
                for (var t = midnight.AddSeconds(first); t < b.Span.EndUtc; t = t.AddSeconds(step))
                    if (t > b.Span.StartUtc) cand.Add((XOf(b, t), t.ToUniversalTime(), false));
            }
            cand.Add((b.X1, b.Span.EndUtc, true));
        }

        // Keep edges first, then fill in round times where they fit.
        var kept = new List<(double X, DateTimeOffset Utc, bool Edge)>();
        foreach (var c in cand.Where(c => c.Edge).Concat(cand.Where(c => !c.Edge)))
            if (kept.All(k => Math.Abs(k.X - c.X) >= MinLabelSpacing && Minute(k.Utc) != Minute(c.Utc))) kept.Add(c);   // no two labels alike
        kept.Sort((a, b) => a.X.CompareTo(b.X));

        var crossesDay = blocks.Count > 0
            && TimeZoneInfo.ConvertTime(blocks[0].Span.StartUtc, zone).Date != TimeZoneInfo.ConvertTime(blocks[^1].Span.EndUtc, zone).Date;
        var ticks = new List<StripTick>();
        DateTime? lastDay = null;
        foreach (var k in kept)
        {
            var local = TimeZoneInfo.ConvertTime(k.Utc, zone);
            var withDate = crossesDay && local.Date != lastDay;
            lastDay = local.Date;
            var label = withDate
                ? local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("HH:mm", CultureInfo.InvariantCulture);
            ticks.Add(new(k.X, label, k.Edge));
        }
        return ticks;
    }
}
