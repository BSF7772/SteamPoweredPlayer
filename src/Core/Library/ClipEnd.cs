// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamPoweredPlayer.Core.Library;

/// <summary>
/// Where a saved clip is meant to end (docs/steam-recording-format.md, "Saved clips keep whole chunks"). Steam copies the 3 s chunks
/// that cover the clip, so the last one runs 0 to 3 s past the end Steam shows, in picture and sound alike. The chunks themselves don't
/// say where the clip ends: only the clip's own <c>session.mpd</c> does (<c>Period start</c> + <c>mediaPresentationDuration</c>, on the
/// chunks' tfdt axis; <c>clip.pb</c> repeats the duration but not the start). Pure: no I/O.
/// </summary>
public static partial class ClipEnd
{
    /// <summary>
    /// Measured on 196 real clips: the tail past the intended end is 0.04 to 3.03 s. A longer tail means the manifest and the chunks don't
    /// belong together, and nothing is cut.
    /// </summary>
    public const double ToleranceSeconds = 0.1;

    /// <summary>
    /// The intended end on the media axis, or null when the manifest can't say or doesn't fit the chunks: live (<c>dynamic</c>), zero-filled
    /// or incomplete, no <c>Period start</c> or duration, or an end that is not before <paramref name="lastChunkEndSeconds"/> by less than
    /// one chunk (+ <see cref="ToleranceSeconds"/>).
    /// </summary>
    /// <param name="mpd">The start of <c>session.mpd</c> (the first few KB are enough).</param>
    /// <param name="lastChunkEndSeconds">End of the last complete chunk on the media axis (<see cref="SessionMedia.ChunkEndSeconds"/>).</param>
    public static double? FromManifest(string? mpd, double lastChunkEndSeconds, double segmentSeconds)
    {
        if (string.IsNullOrWhiteSpace(mpd) || !double.IsFinite(lastChunkEndSeconds) || !(segmentSeconds > 0)) return null;
        var tag = Element(mpd, "MPD");
        if (tag is null || Attribute(tag, "type") is "dynamic") return null;
        if (ParseDuration(Attribute(tag, "mediaPresentationDuration")) is not { } duration || duration <= 0) return null;
        var period = Element(mpd, "Period");
        if (period is null || ParseDuration(Attribute(period, "start")) is not { } start) return null;
        var end = start + duration;
        var tail = lastChunkEndSeconds - end;
        return tail > 0 && tail < segmentSeconds + ToleranceSeconds ? end : null;
    }

    /// <summary><paramref name="media"/> with its <see cref="SessionMedia.EndLimitSeconds"/> from <paramref name="mpd"/>; the same instance when there is none.</summary>
    public static SessionMedia Apply(SessionMedia media, string? mpd)
    {
        ArgumentNullException.ThrowIfNull(media);
        if (media.LastChunk is not { } last) return media;
        return FromManifest(mpd, media.ChunkEndSeconds(last), media.SegmentSeconds) is { } end ? media with { EndLimitSeconds = end } : media;
    }

    /// <summary>An ISO 8601 time duration (<c>PT1H2M3.5S</c>, <c>PT10.092S</c>) in seconds; null for anything else (days, negatives, text).</summary>
    public static double? ParseDuration(string? text)
    {
        if (text is null) return null;
        var m = DurationRegex().Match(text);
        if (!m.Success || m.Groups.Cast<Group>().Skip(1).All(g => !g.Success)) return null;
        double total = 0;
        if (m.Groups[1].Success) total += double.Parse(m.Groups[1].Value, CultureInfo.InvariantCulture) * 3600;
        if (m.Groups[2].Success) total += double.Parse(m.Groups[2].Value, CultureInfo.InvariantCulture) * 60;
        if (m.Groups[3].Success) total += double.Parse(m.Groups[3].Value, CultureInfo.InvariantCulture);
        return double.IsFinite(total) ? total : null;
    }

    /// <summary>The text of the first <c>&lt;name …&gt;</c> start tag, or null.</summary>
    private static string? Element(string xml, string name)
    {
        var m = Regex.Match(xml, "<" + name + @"(?=[\s>/])[^>]*>", RegexOptions.CultureInvariant);
        return m.Success ? m.Value : null;
    }

    private static string? Attribute(string tag, string name)
    {
        var m = Regex.Match(tag, @"\s" + name + @"\s*=\s*(?:""([^""]*)""|'([^']*)')", RegexOptions.CultureInvariant);
        return m.Success ? (m.Groups[1].Success ? m.Groups[1].Value : m.Groups[2].Value) : null;
    }

    [GeneratedRegex(@"^PT(?:(\d+(?:\.\d+)?)H)?(?:(\d+(?:\.\d+)?)M)?(?:(\d+(?:\.\d+)?)S)?$", RegexOptions.CultureInvariant)]
    private static partial Regex DurationRegex();
}
