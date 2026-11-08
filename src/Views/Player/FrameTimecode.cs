// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using SteamPoweredPlayer.Core.Diagnostics;

namespace SteamPoweredPlayer.Views.Player;

/// <summary>
/// The Player's timecode (plan 2026-11-07 F2/F3): fixed width <c>HH:MM:SS.FF</c>, where FF is the 1-based number of the frame within its
/// second (01–60 at 60 fps, 01–30 at 30 fps; 59.94 fps counts 01–60). Pure (no XAML), so the tests and <c>--realcheck</c> use it directly.
/// </summary>
/// <remarks>
/// A frame is shown from its pts until the next frame's, so a position belongs to the frame interval that contains it (floor), with a
/// tolerance of <see cref="ToleranceFrames"/> for Steam's millisecond-rounded pts (a frame at 12 + 1/60 s is stored as 12.017 s or
/// 12.016 s). Plain nearest-frame rounding would label the head of a clip whose first frame sits 0.66 frame after the axis origin (seen on
/// a real clip: 11 ms at 60 fps) as frame 02. One frame step moves FF by exactly one; it wraps from the last frame of a second to 01.
/// </remarks>
public sealed class FrameTimecode : ITimecodeFormatter
{
    /// <summary>A pts up to this fraction of a frame before a frame boundary counts as that frame (ms rounding is ≤ 0.03 frame at 60 fps).</summary>
    public const double ToleranceFrames = 0.2;

    /// <summary>Used when the stream's rate is unknown (no video yet, or mpv reports nothing usable).</summary>
    public const double FallbackFps = 60;

    public static FrameTimecode Instance { get; } = new();

    string ITimecodeFormatter.Format(double seconds, double fps) => Position(seconds, fps);

    /// <summary>A position on the UI axis (0 = head) as <c>HH:MM:SS.FF</c>, FF 1-based.</summary>
    public static string Position(double seconds, double fps)
    {
        var (whole, frame) = Split(seconds, fps, ToleranceFrames);
        return Text(whole, frame + 1);
    }

    /// <summary>A length (the duration, the A–B span) as <c>HH:MM:SS.FF</c>, FF = whole frames past the second (0-based): 6 s → 00:00:06.00.</summary>
    public static string Length(double seconds, double fps)
    {
        var (whole, frame) = Split(seconds, fps, 0.5);
        return Text(whole, frame);
    }

    /// <summary><c>Position / Length</c>, the transport's timecode.</summary>
    public static string PositionOfLength(double position, double length, double fps) => Position(position, fps) + " / " + Length(length, fps);

    /// <summary>A usable frame rate: <paramref name="fps"/> when it is a real rate, else <see cref="FallbackFps"/>.</summary>
    public static double Sanitize(double fps) => fps is > 1 and < 1000 ? fps : FallbackFps;

    private static readonly double[] StandardRates = [24000.0 / 1001, 24, 25, 30000.0 / 1001, 30, 48, 50, 60000.0 / 1001, 60, 72, 90, 100, 120000.0 / 1001, 120, 144, 240];

    /// <summary>
    /// The stream's nominal rate: <see cref="Sanitize"/>d, then snapped to the nearest standard rate within 3 %. A variable-frame-rate clip
    /// reports its average (mpv <c>container-fps</c> 59.09) while its pts steps are 1/59.94 s; numbering frames with the average repeats
    /// one FF and skips another, numbering them with the nominal rate moves FF by exactly one per frame.
    /// </summary>
    public static double Nominal(double fps)
    {
        fps = Sanitize(fps);
        var best = fps; var bestErr = fps * 0.03;
        foreach (var r in StandardRates)
        {
            var err = Math.Abs(r - fps);
            if (err <= bestErr) { best = r; bestErr = err; }
        }
        return best;
    }

    /// <summary>The whole second and the 0-based frame within it of the frame that <paramref name="seconds"/> falls on.</summary>
    public static (long Whole, int Frame) Split(double seconds, double fps, double tolerance)
    {
        fps = Nominal(fps);
        if (!double.IsFinite(seconds) || seconds < 0) seconds = 0;
        var n = (long)Math.Floor(seconds * fps + tolerance);           // the frame number from the head
        var t = n / fps;                                                // that frame's ideal time
        var whole = (long)Math.Floor(t + 1e-9);
        var frame = (int)Math.Floor((t - whole) * fps + 1e-6);             // 59.94 fps: frames don't start on the second
        var perSecond = (int)Math.Ceiling(fps - 1e-6);
        if (frame >= perSecond) { whole++; frame = 0; }                 // float noise at the end of a second
        return (whole, Math.Max(0, frame));
    }

    private static string Text(long whole, int ff) =>
        string.Create(CultureInfo.InvariantCulture, $"{whole / 3600:00}:{whole / 60 % 60:00}:{whole % 60:00}.{ff:00}");
}
