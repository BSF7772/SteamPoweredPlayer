// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace SteamPoweredPlayer.Views.Player;

/// <summary>One ruler tick: its time on the UI axis, its x in the strip, whether it is a major (labelled) tick, and the label.</summary>
public sealed record RulerTick(double Seconds, double X, bool Major, string? Label);

/// <summary>The ruler for one visible window: the major step, how many minor intervals each major holds, and every tick to draw.</summary>
public sealed record RulerLayout(double MajorStepSeconds, int MinorPerMajor, bool Frames, IReadOnlyList<RulerTick> Ticks);

/// <summary>
/// Tick layout for the timeline ruler (Phase 2.2 P4). Pure (no XAML): the same maths serves the control, the tests and <c>--uicheck</c>.
/// </summary>
/// <remarks>
/// The major step is the smallest rung of a nice ladder (frames when deeply zoomed, then 1 s … 2 h) whose labels fit with a gap, so labels
/// never overlap and sit about one label apart (the ladder's ratios are ≤ 2.5, so the gap is at most 2.5× the minimum). Minor ticks are 5 or 10
/// per major (fewer for frame steps), never closer than <see cref="MinMinorPx"/>.
/// </remarks>
public static class TimelineRuler
{
    /// <summary>Least distance between two neighbouring labels' starts, even for short labels.</summary>
    public const double MinLabelSpacingPx = 80;

    /// <summary>Breathing room added to the label width for the minimum spacing.</summary>
    public const double LabelGapPx = 16;

    /// <summary>Least distance between two minor ticks.</summary>
    public const double MinMinorPx = 6;

    private static readonly double[] SecondSteps = [1, 2, 5, 10, 15, 30, 60, 120, 300, 600, 900, 1800, 3600, 7200];
    private static readonly int[] SecondMinor = [10, 5, 5, 10, 5, 5, 10, 10, 5, 10, 5, 10, 10, 10];
    private static readonly int[] FrameSteps = [1, 2, 5, 10, 15, 30];
    private static readonly int[] FrameMinor = [1, 2, 5, 5, 5, 5];

    /// <summary>The least pixels between two labels' starts for a label of <paramref name="labelWidth"/> px.</summary>
    public static double MinSpacing(double labelWidth) => Math.Max(MinLabelSpacingPx, labelWidth + LabelGapPx);

    /// <summary>
    /// Ticks for the window [<paramref name="start"/>, <paramref name="end"/>] seconds drawn over <paramref name="width"/> px.
    /// A tick is included while its label (drawn right of the tick) could still show, i.e. from one label width before the window.
    /// </summary>
    public static RulerLayout Layout(double start, double end, double width, double labelWidth, double fps)
    {
        fps = FrameTimecode.Nominal(fps);
        if (!double.IsFinite(start) || !double.IsFinite(end) || !(width > 0) || !(end > start))
            return new RulerLayout(1, 10, false, []);

        var pxPerSec = width / (end - start);
        var minPx = MinSpacing(labelWidth);

        // pick the smallest rung that fits: frames first (only sub-second ones), then whole seconds and up
        double step = 0; var minor = 10; var frames = false;
        for (var i = 0; i < FrameSteps.Length && step == 0; i++)
        {
            if (FrameSteps[i] >= fps) break;                         // frame rungs stay under a second
            var s = FrameSteps[i] / fps;
            if (s * pxPerSec >= minPx) { step = s; minor = FrameMinor[i]; frames = true; }
        }
        for (var i = 0; i < SecondSteps.Length && step == 0; i++)
            if (SecondSteps[i] * pxPerSec >= minPx) { step = SecondSteps[i]; minor = SecondMinor[i]; }
        if (step == 0) { step = SecondSteps[^1]; minor = SecondMinor[^1]; }

        // drop minors that would sit closer than MinMinorPx
        while (minor > 1 && step * pxPerSec / minor < MinMinorPx) minor = minor >= 10 ? 5 : 1;

        var sub = step / minor;
        var from = (long)Math.Ceiling((start - labelWidth / pxPerSec) / sub - 1e-9);
        var to = (long)Math.Floor(end / sub + 1e-9);
        if (from < 0) from = 0;                                      // the axis starts at 0
        var ticks = new List<RulerTick>((int)Math.Clamp(to - from + 1, 0, 4096));
        for (var n = from; n <= to && ticks.Count < 4096; n++)
        {
            var t = n * sub;
            var major = n % minor == 0;
            ticks.Add(new RulerTick(t, (t - start) * pxPerSec, major, major ? Label(t, frames, fps) : null));
        }
        return new RulerLayout(step, minor, frames, ticks);
    }

    /// <summary><c>M:SS</c> under an hour, <c>H:MM:SS</c> from an hour; sub-second steps add the frame (<c>M:SS.FF</c>, 1-based like the transport).</summary>
    public static string Label(double seconds, bool frames, double fps)
    {
        if (frames)
        {
            var (w, f) = FrameTimecode.Split(seconds, fps, 0.5);
            return string.Create(CultureInfo.InvariantCulture, $"{WholeText(w)}.{f + 1:00}");
        }
        return WholeText((long)Math.Floor(seconds + 1e-6));
    }

    private static string WholeText(long s) => s >= 3600
        ? string.Create(CultureInfo.InvariantCulture, $"{s / 3600}:{s / 60 % 60:00}:{s % 60:00}")
        : string.Create(CultureInfo.InvariantCulture, $"{s / 60}:{s % 60:00}");
}
