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

/// <summary>A label under the axis at <see cref="X"/> (local time). <see cref="Relative"/> is the age of a block start ("10 h ago"), shown under the exact start.</summary>
public readonly record struct StripTick(double X, string Label, bool IsEdge, string? Relative = null);

/// <summary>A local-midnight line at <see cref="X"/>; <see cref="Label"/> is the day that starts there ("Mon Nov 9").</summary>
public readonly record struct StripMidnight(double X, string Label);

/// <summary>The visible break between the older-than-24-h region (left) and the last 24 h (right), from <see cref="X0"/> to <see cref="X1"/> px.</summary>
public readonly record struct StripBreak(double X0, double X1);

/// <summary>
/// <see cref="Cutoff"/> is set when the strip has an older-than-24-h region (it ends at <see cref="StripBreak.X0"/>; the last 24 h start
/// at <see cref="StripBreak.X1"/>). <see cref="RecentEmpty"/>: nothing was recorded in the last 24 h. <see cref="NowX"/> is the right
/// edge: the clock-anchored axis ends at now (0 for the layout without a clock).
/// </summary>
public sealed record StripLayout(IReadOnlyList<StripBlock> Blocks, IReadOnlyList<StripGap> Gaps, IReadOnlyList<StripTick> Ticks)
{
    public static StripLayout Empty { get; } = new([], [], []);
    public IReadOnlyList<StripMidnight> Midnights { get; init; } = [];
    public StripBreak? Cutoff { get; init; }
    public bool RecentEmpty { get; init; }
    public double NowX { get; init; }
}

/// <summary>
/// Lays out the Library page's cache strip (F9, L1): every cache session is a block on a time axis scaled to the real span
/// (minutes to days). Long gaps between sessions are compressed to a short break so a session from yesterday does not squeeze
/// today's into a sliver; the gap stays visible. Pure (no XAML) with an injected clock, so it is unit-tested.
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

    /// <summary>Sessions that ended more than this before <c>now</c> go into the left <see cref="OldShare"/> of the strip.</summary>
    public static readonly TimeSpan RecentWindow = TimeSpan.FromHours(24);
    public const double OldShare = 0.25;
    /// <summary>Width of the visible break between the old and the recent region.</summary>
    public const double CutoffBreakWidth = 24;
    /// <summary>Anchored labels carry a day name and an age, so they are wider.</summary>
    public const double MinAnchoredLabelSpacing = 100;

    private static readonly double[] TickSteps = [60, 120, 300, 600, 900, 1800, 3600, 7200, 10800, 21600, 43200, 86400, 172800];

    private sealed record Seg(DateTimeOffset T0, DateTimeOffset T1, double X0, double X1);
    private sealed record Region(List<StripBlock> Blocks, List<StripGap> Gaps, List<Seg> Segs);

    /// <summary>The layout without a clock: the axis runs from the first block to the end of the last one.</summary>
    public static StripLayout Build(IReadOnlyList<StripSpan> spans, double width, TimeZoneInfo zone)
    {
        if (spans.Count == 0 || !(width > 2 * MinBlockWidth)) return StripLayout.Empty;
        var order = Enumerable.Range(0, spans.Count).OrderBy(i => spans[i].StartUtc).ToArray();
        var r = LayoutRegion(spans, order, 0, width, null);
        return new(r.Blocks, r.Gaps, Ticks(r.Blocks, zone, null, 0, MinLabelSpacing));
    }

    /// <summary>
    /// L1: the layout anchored at <paramref name="now"/> (injected clock). The axis ends at now; sessions that ended more than
    /// <see cref="RecentWindow"/> ago are compressed into the left <see cref="OldShare"/> (with a visible break), the last 24 h use the
    /// rest. Without older sessions the last 24 h use the whole width. Long gaps are still compressed inside each region.
    /// </summary>
    public static StripLayout Build(IReadOnlyList<StripSpan> spans, double width, TimeZoneInfo zone, DateTimeOffset now)
    {
        if (spans.Count == 0 || !(width > 2 * MinBlockWidth)) return StripLayout.Empty;
        var lastEnd = spans.Max(s => s.EndUtc);
        if (lastEnd > now) now = lastEnd;                                       // clock skew: never draw past the axis end
        var cutoff = now - RecentWindow;
        var all = Enumerable.Range(0, spans.Count).OrderBy(i => spans[i].StartUtc).ToArray();
        var old = all.Where(i => spans[i].EndUtc < cutoff).ToArray();
        var recent = all.Where(i => spans[i].EndUtc >= cutoff).ToArray();

        var blocks = new List<StripBlock>();
        var gaps = new List<StripGap>();
        var midnights = new List<StripMidnight>();
        StripBreak? brk = null;
        var recentX0 = 0.0;
        if (old.Length > 0)
        {
            var oldW = Math.Max(MinBlockWidth * old.Length, OldShare * width - CutoffBreakWidth);
            var o = LayoutRegion(spans, old, 0, oldW, null);
            blocks.AddRange(o.Blocks);
            gaps.AddRange(o.Gaps);
            recentX0 = oldW + CutoffBreakWidth;
            brk = new(oldW, recentX0);
        }
        if (recent.Length > 0)
        {
            var r = LayoutRegion(spans, recent, recentX0, Math.Max(MinBlockWidth * recent.Length, width - recentX0), now);
            blocks.AddRange(r.Blocks);
            gaps.AddRange(r.Gaps);
            midnights = LocalMidnights(r, zone, now);
        }
        return new(blocks, gaps, Ticks(blocks, zone, now, width, MinAnchoredLabelSpacing))
        {
            Midnights = midnights, Cutoff = brk, RecentEmpty = recent.Length == 0, NowX = width,
        };
    }

    /// <summary>
    /// Piecewise axis over <paramref name="order"/> (indices into <paramref name="spans"/>, in time order): blocks keep their real
    /// length, a long gap shrinks to a fixed break. <paramref name="endAt"/> adds the gap after the last block up to that time.
    /// The result fills exactly [<paramref name="xa"/>, <paramref name="xa"/> + <paramref name="width"/>].
    /// </summary>
    private static Region LayoutRegion(IReadOnlyList<StripSpan> spans, int[] order, double xa, double width, DateTimeOffset? endAt)
    {
        var recorded = order.Sum(i => Math.Max(spans[i].Seconds, 1));
        var compressAbove = Math.Max(recorded * CompressShare, 600);
        // gaps[k - 1] sits before block k; with endAt the last entry is the trailing gap up to endAt
        var gaps = new List<(double Seconds, bool Compressed)>();
        for (var k = 1; k < order.Length; k++)
        {
            var g = Math.Max(0, (spans[order[k]].StartUtc - spans[order[k - 1]].EndUtc).TotalSeconds);
            gaps.Add((g, g > compressAbove));
        }
        if (endAt is { } e)
        {
            var g = Math.Max(0, (e - spans[order[^1]].EndUtc).TotalSeconds);
            gaps.Add((g, g > compressAbove));
        }
        var fixedPx = gaps.Sum(g => g.Compressed ? BreakWidth : MinGapWidth * (g.Seconds > 0 ? 1 : 0));
        var flexSeconds = recorded + gaps.Where(g => !g.Compressed).Sum(g => g.Seconds);
        var minBlocksPx = order.Length * MinBlockWidth;
        var pxPerSecond = Math.Max(0, width - fixedPx - minBlocksPx) / Math.Max(flexSeconds, 1);
        double GapWidth((double Seconds, bool Compressed) g) =>
            g.Compressed ? BreakWidth : g.Seconds > 0 ? MinGapWidth + g.Seconds * pxPerSecond : 0;

        var blocks = new List<StripBlock>();
        var gapList = new List<StripGap>();
        var segs = new List<Seg>();
        var x = 0.0;
        for (var k = 0; k < order.Length; k++)
        {
            if (k > 0)
            {
                var g = gaps[k - 1];
                var gw = GapWidth(g);
                if (gw > 0) gapList.Add(new(x, x + gw, TimeSpan.FromSeconds(g.Seconds), g.Compressed));
                segs.Add(new(spans[order[k - 1]].EndUtc, spans[order[k]].StartUtc, x, x + gw));
                x += gw;
            }
            var s = spans[order[k]];
            var bw = MinBlockWidth + Math.Max(s.Seconds, 1) * pxPerSecond;
            blocks.Add(new(order[k], x, x + bw, s));
            segs.Add(new(s.StartUtc, s.EndUtc, x, x + bw));
            x += bw;
        }
        if (endAt is { } end)
        {
            var g = gaps[^1];
            var gw = GapWidth(g);
            if (gw > 0) gapList.Add(new(x, x + gw, TimeSpan.FromSeconds(g.Seconds), g.Compressed));
            segs.Add(new(spans[order[^1]].EndUtc, end, x, x + gw));
            x += gw;
        }
        // rounding: stretch the result exactly to the width, then shift it to xa
        var scale = x > 0 ? width / x : 1;
        return new(
            [.. blocks.Select(b => b with { X0 = xa + b.X0 * scale, X1 = xa + b.X1 * scale })],
            [.. gapList.Select(g => g with { X0 = xa + g.X0 * scale, X1 = xa + g.X1 * scale })],
            [.. segs.Select(sg => sg with { X0 = xa + sg.X0 * scale, X1 = xa + sg.X1 * scale })]);
    }

    private static double MapX(Region r, DateTimeOffset t)
    {
        foreach (var sg in r.Segs)
        {
            if (t < sg.T0 || t > sg.T1) continue;
            var len = (sg.T1 - sg.T0).TotalSeconds;
            return sg.X0 + (len <= 0 ? 0 : (t - sg.T0).TotalSeconds / len) * (sg.X1 - sg.X0);
        }
        return t < r.Segs[0].T0 ? r.Segs[0].X0 : r.Segs[^1].X1;
    }

    /// <summary>Local midnights inside the recent region (DST-safe: a midnight the clocks skipped is the next valid time).</summary>
    private static List<StripMidnight> LocalMidnights(Region r, TimeZoneInfo zone, DateTimeOffset now)
    {
        var res = new List<StripMidnight>();
        var first = r.Segs[0].T0;
        var day = TimeZoneInfo.ConvertTime(first, zone).Date.AddDays(1);
        var lastX = double.NegativeInfinity;
        for (var i = 0; i < 8; i++, day = day.AddDays(1))
        {
            var mid = LocalMidnight(day, zone);
            if (mid <= first || mid >= now) break;
            var x = MapX(r, mid);
            if (x - lastX < MinLabelSpacing / 2) continue;
            lastX = x;
            res.Add(new(x, day.ToString("ddd MMM d", CultureInfo.InvariantCulture)));
        }
        return res;
    }

    /// <summary>The instant the local calendar day <paramref name="date"/> starts in <paramref name="zone"/>.</summary>
    public static DateTimeOffset LocalMidnight(DateTime date, TimeZoneInfo zone)
    {
        var dt = DateTime.SpecifyKind(date.Date, DateTimeKind.Unspecified);
        if (zone.IsInvalidTime(dt)) dt = dt.AddHours(1);
        return new DateTimeOffset(dt, zone.GetUtcOffset(dt));
    }

    /// <summary>"Today" / "Yesterday" / "Wed Nov 4" (with the year when it is not this year), by the local calendar day.</summary>
    public static string DayLabel(DateTimeOffset utc, DateTimeOffset nowUtc, TimeZoneInfo zone)
    {
        var local = TimeZoneInfo.ConvertTime(utc, zone).Date;
        var today = TimeZoneInfo.ConvertTime(nowUtc, zone).Date;
        if (local == today) return "Today";
        if (local == today.AddDays(-1)) return "Yesterday";
        return local.ToString(local.Year == today.Year ? "ddd MMM d" : "ddd MMM d, yyyy", CultureInfo.InvariantCulture);
    }

    /// <summary>The exact start in local time: <c>Today 09:00</c>, <c>Yesterday 22:37</c>, <c>Wed Nov 4 22:37</c>.</summary>
    public static string StartLabel(DateTimeOffset utc, DateTimeOffset nowUtc, TimeZoneInfo zone) =>
        DayLabel(utc, nowUtc, zone) + " " + TimeZoneInfo.ConvertTime(utc, zone).ToString("HH:mm", CultureInfo.InvariantCulture);

    /// <summary>"just now", "12 min ago", "10 h ago", "3 d ago". A start in the future (clock skew) reads "just now".</summary>
    public static string Age(TimeSpan age)
    {
        if (age < TimeSpan.FromMinutes(1)) return "just now";
        if (age < TimeSpan.FromHours(1)) return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalMinutes} min ago");
        if (age < TimeSpan.FromHours(24)) return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalHours} h ago");
        return string.Create(CultureInfo.InvariantCulture, $"{(int)age.TotalDays} d ago");
    }

    /// <summary>Where wall-clock time <paramref name="utc"/> falls inside <paramref name="block"/> (clamped to it).</summary>
    public static double XOf(StripBlock block, DateTimeOffset utc)
    {
        var len = block.Span.Seconds;
        var f = len <= 0 ? 0 : Math.Clamp((utc - block.Span.StartUtc).TotalSeconds / len, 0, 1);
        return block.X0 + f * (block.X1 - block.X0);
    }

    private static long Minute(DateTimeOffset t) => t.ToUnixTimeSeconds() / 60;

    private static List<StripTick> Ticks(List<StripBlock> blocks, TimeZoneInfo zone, DateTimeOffset? now, double nowX, double spacing)
    {
        // Candidates. Kind: 0 = now, 1 = block start, 2 = block end, 3 = round time inside a block wide enough for it.
        var cand = new List<(double X, DateTimeOffset Utc, int Kind)>();
        if (now is { } n0) cand.Add((nowX, n0, 0));
        foreach (var b in blocks)
        {
            cand.Add((b.X0, b.Span.StartUtc, 1));
            var px = b.X1 - b.X0;
            var len = b.Span.Seconds;
            if (len > 0 && px > 2 * spacing)
            {
                var want = len * spacing * 1.5 / px;                           // seconds per label at a comfortable density
                var step = TickSteps.FirstOrDefault(t => t >= want, TickSteps[^1]);
                var local = TimeZoneInfo.ConvertTime(b.Span.StartUtc, zone);
                var midnight = new DateTimeOffset(local.Date, local.Offset);
                var first = Math.Ceiling((local - midnight).TotalSeconds / step) * step;
                for (var t = midnight.AddSeconds(first); t < b.Span.EndUtc; t = t.AddSeconds(step))
                    if (t > b.Span.StartUtc) cand.Add((XOf(b, t), t.ToUniversalTime(), 3));
            }
            cand.Add((b.X1, b.Span.EndUtc, 2));
        }

        // Important labels first, then round times where they fit. Anchored: now, then the newest starts, then the ends.
        IEnumerable<(double X, DateTimeOffset Utc, int Kind)> ordered = now is null
            ? cand.Where(c => c.Kind is 1 or 2).Concat(cand.Where(c => c.Kind == 3))
            : cand.OrderBy(c => c.Kind).ThenByDescending(c => c.Kind == 1 ? c.X : 0).ThenBy(c => c.X);
        var kept = new List<(double X, DateTimeOffset Utc, int Kind)>();
        foreach (var c in ordered)
            if (kept.All(k => Math.Abs(k.X - c.X) >= spacing && (c.Kind == 0 || k.Kind == 0 || Minute(k.Utc) != Minute(c.Utc)))) kept.Add(c);
        kept.Sort((a, b) => a.X.CompareTo(b.X));

        var ticks = new List<StripTick>();
        if (now is { } nowUtc)
        {
            DateTime? lastDay = null;
            foreach (var k in kept)
            {
                if (k.Kind == 0) { ticks.Add(new(k.X, "Now", true)); continue; }
                var local = TimeZoneInfo.ConvertTime(k.Utc, zone);
                if (k.Kind == 1)
                {
                    ticks.Add(new(k.X, StartLabel(k.Utc, nowUtc, zone), true, Age(nowUtc - k.Utc)));
                    lastDay = local.Date;
                    continue;
                }
                var clock = local.ToString("HH:mm", CultureInfo.InvariantCulture);
                ticks.Add(new(k.X, local.Date != lastDay ? DayLabel(k.Utc, nowUtc, zone) + " " + clock : clock, k.Kind == 2));
                lastDay = local.Date;
            }
            return ticks;
        }

        var crossesDay = blocks.Count > 0
            && TimeZoneInfo.ConvertTime(blocks[0].Span.StartUtc, zone).Date != TimeZoneInfo.ConvertTime(blocks[^1].Span.EndUtc, zone).Date;
        DateTime? prevDay = null;
        foreach (var k in kept)
        {
            var local = TimeZoneInfo.ConvertTime(k.Utc, zone);
            var withDate = crossesDay && local.Date != prevDay;
            prevDay = local.Date;
            var label = withDate
                ? local.ToString("MMM d HH:mm", CultureInfo.InvariantCulture)
                : local.ToString("HH:mm", CultureInfo.InvariantCulture);
            ticks.Add(new(k.X, label, k.Kind != 3));
        }
        return ticks;
    }
}
