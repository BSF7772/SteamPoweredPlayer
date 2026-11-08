// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>
/// The player's timecode formatter (plan 2026-11-07 F2/F3), plugged into <c>--realcheck</c> by the app: <see cref="RealCheck.Formatter"/>.
/// Until one is registered the timecode case reports SKIP.
/// </summary>
public interface ITimecodeFormatter
{
    /// <summary><paramref name="seconds"/> on the UI axis (0 = first frame) as <c>HH:MM:SS.FF</c> for a stream of <paramref name="fps"/>.</summary>
    string Format(double seconds, double fps);
}

/// <summary>
/// F2/F3: fixed width <c>HH:MM:SS.FF</c>; FF is the 1-based frame number within the second (01..fps); the nearest frame is used, so
/// millisecond-rounded pts (Steam's timebase) still give the right frame; one frame step moves exactly one FF. Pure.
/// </summary>
public static partial class TimecodeRules
{
    [GeneratedRegex(@"^\d{2}:[0-5]\d:[0-5]\d\.\d{2}$", RegexOptions.CultureInvariant)]
    private static partial Regex Shape();

    /// <summary>Problems found (empty = PASS). Each problem names the input and the output.</summary>
    public static IReadOnlyList<string> Check(ITimecodeFormatter formatter, double fps)
    {
        ArgumentNullException.ThrowIfNull(formatter);
        var problems = new List<string>();
        var perSecond = (int)Math.Round(fps);
        string F(double s)
        {
            try { return formatter.Format(s, fps) ?? ""; }
            catch (Exception e) { return "<" + e.GetType().Name + ">"; }
        }
        void Expect(double s, string expected)
        {
            var got = F(s);
            if (got != expected) problems.Add(string.Create(CultureInfo.InvariantCulture, $"{s:0.000} s at {fps:0.##} fps gave \"{got}\", expected \"{expected}\""));
        }

        Expect(0, "00:00:00.01");
        Expect(1, "00:00:01.01");
        Expect(1530 + 3.0 / fps, "00:25:30.04");
        Expect(6000, "01:40:00.01");
        Expect((perSecond - 1) / fps, "00:00:00." + perSecond.ToString("00", CultureInfo.InvariantCulture));

        // every frame of one second, from millisecond-rounded pts: shape, fixed width, FF = 1-based frame, one step = one FF
        var width = -1;
        for (var k = 0; k < perSecond * 2 && problems.Count < 8; k++)
        {
            var pts = Math.Round(12 + k / fps, 3);
            var s = F(pts);
            if (!Shape().IsMatch(s)) { problems.Add($"\"{s}\" is not HH:MM:SS.FF"); continue; }
            if (width < 0) width = s.Length;
            else if (s.Length != width) problems.Add($"\"{s}\" changes the width");
            var ff = int.Parse(s[^2..], CultureInfo.InvariantCulture);
            var expected = k % perSecond + 1;
            if (ff != expected)
                problems.Add(string.Create(CultureInfo.InvariantCulture, $"frame {k} ({pts:0.000} s) shows FF {ff:00}, expected frame number {expected:00} (1-based)"));
        }
        return problems;
    }
}

/// <summary>Result of an A-B loop observation: <see cref="Wraps"/> jumps from near B back to A, <see cref="MaxPastB"/> how far it ran past B.</summary>
public sealed record AbLoopResult(int Samples, int Wraps, double MaxPastB, double MinBeforeA, bool Ok)
{
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Samples} samples, {Wraps} wrap(s), max {MaxPastB * 1000:0} ms past B, min {MinBeforeA * 1000:0} ms before A");
}

/// <summary>Result of a loop-length observation: <see cref="Laps"/> completed A→B laps, the worst |span − (B−A)| in seconds.</summary>
public sealed record LoopLengthResult(int Samples, int Laps, double MeanSpan, double MaxSpanError, double Tolerance, bool Ok, string Trace = "")
{
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Samples} samples, {Laps} lap(s), mean span {MeanSpan:0.000} s, max span error {MaxSpanError * 1000:0} ms (tol {Tolerance * 1000:0} ms)")
        + (Trace.Length == 0 ? "" : "; laps first->last: " + Trace);
}

/// <summary>Result of consecutive frame-back steps: the worst |step − one frame| in frames.</summary>
public sealed record FrameBackResult(int Steps, double MaxErrorFrames, bool Ok)
{
    public string Describe() => string.Create(CultureInfo.InvariantCulture,
        $"{Steps} step(s), worst deviation from exactly one frame {MaxErrorFrames:0.00} frame(s)");
}

/// <summary>The pure rules of <c>--realcheck</c>: which recordings to use and what counts as a pass.</summary>
public static class RealCheckRules
{
    /// <summary>A clip needs this much media for +5 s, −5 s and the A-B loop at 1–3 s.</summary>
    public const double MinClipSeconds = 6;

    /// <summary>The cache check plays this much of the active session's end (D-TEST).</summary>
    public const double CacheTailSeconds = 10;

    /// <summary>The shortest saved clip with at least <see cref="MinClipSeconds"/> of media, or null.</summary>
    public static RecordingEntry? ShortestClip(IEnumerable<RecordingEntry> recordings) =>
        recordings.Where(r => r.Kind == RecordingKind.Clip && r.Runs.Count > 0 && r.DurationSeconds >= MinClipSeconds)
            .OrderBy(r => r.DurationSeconds).ThenBy(r => r.RelativeFolder, StringComparer.Ordinal).FirstOrDefault();

    /// <summary>The live cache session, else the newest one; null when there is none.</summary>
    public static RecordingEntry? ActiveCache(IEnumerable<RecordingEntry> recordings) =>
        recordings.Where(r => r.Kind == RecordingKind.CacheSession && r.Runs.Count > 0)
            .OrderByDescending(r => r.Live).ThenByDescending(r => r.StartUtc).FirstOrDefault();

    /// <summary>True when <paramref name="actual"/> is within one frame (plus 1 ms of rounding) of <paramref name="expected"/>.</summary>
    public static bool WithinFrames(double expected, double actual, double fps, double frames = 1) =>
        double.IsFinite(actual) && Math.Abs(actual - expected) <= frames / fps + 0.001;

    /// <summary>
    /// An A-B loop works when the position wraps from near B back to A at least once, never runs more than two frames past B and never
    /// lands more than two frames before A (mpv's ab-loop jumps to A, not to the head).
    /// </summary>
    public static AbLoopResult EvaluateAbLoop(IReadOnlyList<double> positions, double a, double b, double fps)
    {
        var tol = 2 / fps + 0.001;
        int wraps = 0;
        double maxPast = double.NegativeInfinity, minBefore = double.NegativeInfinity;
        var started = false;
        var prev = double.NaN;                                 // last available sample: mpv has no time-pos during the loop's reload
        for (var i = 0; i < positions.Count; i++)
        {
            var p = positions[i];
            if (!double.IsFinite(p)) continue;
            if (!started) { started = true; }
            maxPast = Math.Max(maxPast, p - b);
            minBefore = Math.Max(minBefore, a - p);
            if (double.IsFinite(prev) && prev - p > (b - a) / 2) wraps++;
            prev = p;
        }
        if (!started) return new(0, 0, double.NaN, double.NaN, false);
        var ok = wraps >= 1 && maxPast <= tol && minBefore <= tol;
        return new(positions.Count, wraps, maxPast, minBefore, ok);
    }

    /// <summary>
    /// A loop works when every lap, measured from the player's own position (first sample after a wrap to the last sample before the
    /// next one), is as long as the marked range: within one frame plus two sampling steps, for at least three laps. The first span is dropped:
    /// sampling starts after the seek to A, so that lap is only a partial observation.
    /// </summary>
    public static LoopLengthResult EvaluateLoopLength(IReadOnlyList<double> positions, double a, double b, double fps, double speed, double stepSeconds, int minLaps = 3)
    {
        var tol = 1 / fps + 2 * speed * stepSeconds + 0.001;
        var wrapDrop = 3 / fps;
        var spans = new List<double>();
        var trace = new List<string>();
        var have = false;
        double first = 0, last = 0;
        var count = 0;
        foreach (var p in positions)
        {
            if (!double.IsFinite(p)) continue;
            count++;
            if (have && last - p > wrapDrop) { spans.Add(last - first); trace.Add(string.Create(CultureInfo.InvariantCulture, $"{first:0.000}->{last:0.000}")); have = false; }
            if (!have) { first = p; have = true; }
            last = p;
        }
        if (spans.Count > 0) { spans.RemoveAt(0); trace.RemoveAt(0); }
        if (spans.Count == 0) return new(count, 0, double.NaN, double.NaN, tol, false);
        var want = b - a;
        var maxErr = spans.Max(s => Math.Abs(s - want));
        return new(count, spans.Count, spans.Average(), maxErr, tol, spans.Count >= minLaps && maxErr <= tol, string.Join(" ", trace.Take(8)));
    }

    /// <summary>
    /// Frame-back steps work when every consecutive pair of shown-frame timestamps differs by exactly one frame (±0.35 frame, which
    /// absorbs millisecond rounding). <paramref name="positions"/> is the start followed by the position after each step.
    /// </summary>
    public static FrameBackResult EvaluateFrameBacks(IReadOnlyList<double> positions, double fps)
    {
        if (positions.Count < 2 || positions.Any(p => !double.IsFinite(p))) return new(0, double.NaN, false);
        var worst = 0.0;
        for (var i = 1; i < positions.Count; i++)
            worst = Math.Max(worst, Math.Abs((positions[i - 1] - positions[i]) * fps - 1));
        return new(positions.Count - 1, worst, worst <= 0.35);
    }
}
