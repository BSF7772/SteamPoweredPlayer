// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Steam;

public sealed class MarkersTests
{
    private static readonly DateTimeOffset T0 = new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero);

    private static TimelineFile Tl(params (string id, long time, long dur)[] e) => new(T0, 0, 900000,
        e.Select(x => new TimelineEntry(x.id, x.time, TimelineEntryType.Event, "event", x.id, null, null, null, x.dur, false, null, [], null, null)).ToList(), []);

    private static SessionMedia Media(params (int a, int b)[] runs) =>
        new("x", runs.Select(r => new SegmentRun(r.a, r.b)).ToList(), 3.0, false, true);

    [Fact]
    public void Exact_offset_maps_time_minus_offset()
    {
        var set = Markers.Map(Tl(("a", 70000, 0)), 57332);
        Assert.Equal(MarkerAlignment.Exact, set.Alignment);
        var m = Assert.Single(set.Markers);
        Assert.Equal(12.668, m.StartSeconds, 6);
        Assert.Null(m.EndSeconds);
        Assert.False(m.IsRange);
        Assert.False(m.IsApproximate);
    }

    [Fact]
    public void Exact_offset_wins_over_the_folder_name_fallback()
    {
        var set = Markers.Map(Tl(("a", 70000, 0)), 57332, bgStartUtc: T0.AddSeconds(50));
        Assert.Equal(12.668, set.Markers[0].StartSeconds, 6);
    }

    [Fact]
    public void Fallback_uses_folder_name_difference_and_flags_approximate()
    {
        var set = Markers.Map(Tl(("a", 70000, 0)), null, bgStartUtc: T0.AddSeconds(57));
        Assert.Equal(MarkerAlignment.Approximate, set.Alignment);
        Assert.Equal(13.0, set.Markers[0].StartSeconds, 6);
        Assert.True(set.Markers[0].IsApproximate);
    }

    [Fact]
    public void Fallback_can_take_an_explicit_timeline_start()
    {
        var tl = Tl(("a", 70000, 0)) with { DateRecordedUtc = null };
        Assert.Equal(MarkerAlignment.None, Markers.Map(tl, null, T0.AddSeconds(57)).Alignment);
        var set = Markers.Map(tl, null, T0.AddSeconds(57), timelineStartUtc: T0);
        Assert.Equal(13.0, set.Markers[0].StartSeconds, 6);
    }

    [Fact]
    public void Nothing_known_gives_no_markers_and_a_diagnostic()
    {
        var set = Markers.Map(Tl(("a", 70000, 0)), null);
        Assert.Equal(MarkerAlignment.None, set.Alignment);
        Assert.Empty(set.Markers);
        Assert.Single(set.Diagnostics);
    }

    [Fact]
    public void Entries_with_duration_become_ranges_and_markers_are_sorted()
    {
        var set = Markers.Map(Tl(("late", 100000, 8000), ("early", 10000, 0), ("tie1", 50000, 0), ("tie2", 50000, 0)), 0);
        Assert.Equal(["early", "tie1", "tie2", "late"], set.Markers.Select(m => m.Entry.Id));
        var range = set.Markers[3];
        Assert.True(range.IsRange);
        Assert.Equal(100.0, range.StartSeconds, 6);
        Assert.Equal(108.0, range.EndSeconds!.Value, 6);
    }

    [Fact]
    public void Place_splits_by_covered_chunks_and_reports_the_reason()
    {
        // Runs 1..10 and 14..20 cover [0,30) and [39,60).
        var set = Markers.Map(Tl(("before", -5000, 0), ("in1", 0, 0), ("lastIn1", 29999, 0), ("gap", 30000, 0), ("in2", 39000, 0),
            ("end", 60000, 0), ("after", 90000, 0)), 0);
        var p = Markers.Place(set.Markers, Media((1, 10), (14, 20)));
        Assert.Equal(["in1", "lastIn1", "in2"], p.Visible.Select(m => m.Entry.Id));
        Assert.Equal([("before", TrimReason.BeforeStart), ("gap", TrimReason.InGap), ("end", TrimReason.AfterEnd), ("after", TrimReason.AfterEnd)],
            p.TrimmedAway.Select(t => (t.Marker.Entry.Id!, t.Reason)));
    }

    [Fact]
    public void Ranges_overlapping_covered_media_stay_visible_even_when_partly_trimmed()
    {
        var set = Markers.Map(Tl(("startsBefore", -4000, 8000), ("endsAfter", 56000, 8000), ("exactlyBefore", -8000, 8000),
            ("inGapOnly", 31000, 5000), ("spansGap", 28000, 14000)), 0);
        var p = Markers.Place(set.Markers, Media((1, 10), (14, 20)));
        Assert.Equal(["startsBefore", "spansGap", "endsAfter"], p.Visible.Select(m => m.Entry.Id));
        Assert.Equal([("exactlyBefore", TrimReason.BeforeStart), ("inGapOnly", TrimReason.InGap)],
            p.TrimmedAway.Select(t => (t.Marker.Entry.Id!, t.Reason)));
    }

    [Fact]
    public void Place_with_no_media_trims_everything_and_drops_nothing()
    {
        var set = Markers.Map(Tl(("a", 1000, 0), ("b", 2000, 4000)), 0);
        var p = Markers.Place(set.Markers, Media());
        Assert.Empty(p.Visible);
        Assert.Equal(2, p.TrimmedAway.Count);
    }

    [Fact]
    public void Place_honours_a_non_default_segment_length_and_first_chunk()
    {
        // Chunks 5..6 at 2 s cover [8,12).
        var set = Markers.Map(Tl(("a", 7000, 0), ("b", 8000, 0), ("c", 11999, 0), ("d", 12000, 0)), 0);
        var media = new SessionMedia("x", [new SegmentRun(5, 6)], 2.0, false, true);
        var p = Markers.Place(set.Markers, media);
        Assert.Equal(["b", "c"], p.Visible.Select(m => m.Entry.Id));
    }

    // ---- golden files shared with the Kotlin port ----

    public static TheoryData<string> Goldens => new() { "markers-exact.json", "markers-fallback.json", "markers-clip.json" };

    [Theory]
    [MemberData(nameof(Goldens))]
    public async Task Golden_results_match(string golden)
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "fixtures", "golden");
        using var doc = JsonDocument.Parse(await File.ReadAllBytesAsync(Path.Combine(dir, golden)));
        var root = doc.RootElement;
        var timeline = await Timeline.ReadAsync(Path.GetFullPath(Path.Combine(dir, root.GetProperty("timeline").GetString()!)));
        Assert.Empty(timeline.Diagnostics);

        var off = root.GetProperty("offset");
        long? offsetMs = null;
        switch (off.GetProperty("source").GetString())
        {
            case "gamerecording":
                var gr = await RecordingPb.ReadGameRecordingAsync(Path.GetFullPath(Path.Combine(dir, off.GetProperty("file").GetString()!)));
                offsetMs = gr.OffsetMsFor(off.GetProperty("timelineId").GetString()!);
                break;
            case "clip":
                offsetMs = (await RecordingPb.ReadClipAsync(Path.GetFullPath(Path.Combine(dir, off.GetProperty("file").GetString()!)))).OffsetMs;
                break;
            case "none": break;
            default: Assert.Fail("unknown offset source"); break;
        }
        DateTimeOffset? bg = root.GetProperty("bgStartUtc") is { ValueKind: JsonValueKind.String } s
            ? DateTimeOffset.Parse(s.GetString()!, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal) : null;

        var mediaEl = root.GetProperty("media");
        var runs = mediaEl.GetProperty("runs").EnumerateArray()
            .Select(r => new SegmentRun(r[0].GetInt32(), r[1].GetInt32())).ToList();
        var media = new SessionMedia("x", runs, mediaEl.GetProperty("segmentSeconds").GetDouble(), false, true);

        var set = Markers.Map(timeline, offsetMs, bg);
        var placement = Markers.Place(set.Markers, media);

        var exp = root.GetProperty("expected");
        Assert.Equal(exp.GetProperty("alignment").GetString(), set.Alignment.ToString());
        AssertMarkers(exp.GetProperty("visible"), placement.Visible.Select(m => (m, (string?)null)).ToList());
        AssertMarkers(exp.GetProperty("trimmed"), placement.TrimmedAway.Select(t => (t.Marker, (string?)t.Reason.ToString())).ToList());
    }

    private static void AssertMarkers(JsonElement expected, IReadOnlyList<(Marker m, string? reason)> actual)
    {
        var items = expected.EnumerateArray().ToList();
        Assert.Equal(items.Count, actual.Count);
        for (int i = 0; i < items.Count; i++)
        {
            Assert.Equal(items[i].GetProperty("id").GetString(), actual[i].m.Entry.Id);
            Assert.Equal(items[i].GetProperty("start").GetDouble(), actual[i].m.StartSeconds, 6);
            var end = items[i].GetProperty("end");
            if (end.ValueKind == JsonValueKind.Null) Assert.Null(actual[i].m.EndSeconds);
            else Assert.Equal(end.GetDouble(), actual[i].m.EndSeconds!.Value, 6);
            if (actual[i].reason is not null) Assert.Equal(items[i].GetProperty("reason").GetString(), actual[i].reason);
        }
    }
}
