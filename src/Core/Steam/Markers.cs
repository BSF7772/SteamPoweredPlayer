// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Steam;

/// <summary>How a marker position was derived.</summary>
public enum MarkerAlignment
{
    /// <summary>Steam's exact millisecond offset (protobuf field 1.5.10).</summary>
    Exact,
    /// <summary>From the folder-name times (whole seconds, about ±1 s).</summary>
    Approximate,
    /// <summary>Neither is known: nothing could be mapped.</summary>
    None,
}

/// <summary>
/// A timeline entry placed on the session's media time (tfdt axis, seconds). A range has <see cref="EndSeconds"/> (entries with
/// duration &gt; 0, e.g. <c>cs2_multi_kill</c> ≈ 8 s); a point has <c>null</c>.
/// </summary>
public sealed record Marker(
    TimelineEntry Entry, double StartSeconds, double? EndSeconds, MarkerAlignment Alignment)
{
    public bool IsRange => EndSeconds is not null;
    public bool IsApproximate => Alignment == MarkerAlignment.Approximate;
}

public sealed record MarkerSet(IReadOnlyList<Marker> Markers, MarkerAlignment Alignment, IReadOnlyList<string> Diagnostics);

public enum TrimReason { BeforeStart, AfterEnd, InGap }

public sealed record TrimmedMarker(Marker Marker, TrimReason Reason);

/// <summary>Markers split by what the chunks on disk cover. Nothing is dropped silently: uncovered markers are listed in <see cref="TrimmedAway"/>.</summary>
public sealed record MarkerPlacement(IReadOnlyList<Marker> Visible, IReadOnlyList<TrimmedMarker> TrimmedAway);

/// <summary>Pure mapping from timeline entries to media time (docs/steam-recording-format.md, "Mapping to video time").</summary>
public static class Markers
{
    /// <summary>
    /// <c>position = entry.time/1000 − offsetMs/1000</c> when <paramref name="exactOffsetMs"/> is known; otherwise
    /// <c>entry.time/1000 − (bgStartUtc − timelineStartUtc)</c> in seconds (±1 s, flagged approximate). The timeline start defaults to
    /// the file's <c>daterecorded</c>. Result is sorted by start.
    /// </summary>
    public static MarkerSet Map(TimelineFile timeline, long? exactOffsetMs, DateTimeOffset? bgStartUtc = null, DateTimeOffset? timelineStartUtc = null)
    {
        var d = new List<string>();
        double offsetSeconds;
        MarkerAlignment alignment;
        if (exactOffsetMs is { } ms) { offsetSeconds = ms / 1000.0; alignment = MarkerAlignment.Exact; }
        else if (bgStartUtc is { } bg && (timelineStartUtc ?? timeline.DateRecordedUtc) is { } tl)
        {
            offsetSeconds = (bg - tl).TotalSeconds;
            alignment = MarkerAlignment.Approximate;
        }
        else
        {
            d.Add("no exact offset and no session/timeline start times: markers cannot be placed");
            return new([], MarkerAlignment.None, d);
        }

        var list = new List<Marker>(timeline.Entries.Count);
        foreach (var e in timeline.Entries)
        {
            double start = e.TimeMs / 1000.0 - offsetSeconds;
            list.Add(new(e, start, e.DurationMs > 0 ? start + e.DurationMs / 1000.0 : null, alignment));
        }
        // Stable order: by start, ties keep file order.
        var sorted = list.Select((m, i) => (m, i)).OrderBy(x => x.m.StartSeconds).ThenBy(x => x.i).Select(x => x.m).ToList();
        return new(sorted, alignment, d);
    }

    /// <summary>
    /// Splits markers by the media actually on disk. A point is covered when it lies inside a run (chunk N covers
    /// [(N−1)·seg, N·seg)); a range when it overlaps one (a partly covered range stays visible).
    /// </summary>
    public static MarkerPlacement Place(IEnumerable<Marker> markers, SessionMedia media)
    {
        var runs = media.Runs.Select(r => (Start: Math.Max(media.ChunkStartSeconds(r.First), media.StartSeconds), End: Math.Min(media.ChunkEndSeconds(r.Last), media.EndSeconds))).ToList();
        var visible = new List<Marker>();
        var trimmed = new List<TrimmedMarker>();
        foreach (var m in markers)
        {
            double s = m.StartSeconds, e = m.EndSeconds ?? m.StartSeconds;
            bool covered = runs.Any(r => m.IsRange ? s < r.End && e > r.Start : s >= r.Start && s < r.End);
            if (covered) { visible.Add(m); continue; }
            TrimReason reason =
                runs.Count == 0 || (m.IsRange ? e <= runs[0].Start : s < runs[0].Start) ? TrimReason.BeforeStart
                : s >= runs[^1].End ? TrimReason.AfterEnd
                : TrimReason.InGap;
            trimmed.Add(new(m, reason));
        }
        return new(visible, trimmed);
    }
}
