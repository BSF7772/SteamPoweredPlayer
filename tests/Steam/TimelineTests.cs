// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Steam;

public sealed class TimelineTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "timelines", name);

    [Fact]
    public async Task Basic_timeline_reads_header_and_all_entries()
    {
        var t = await Timeline.ReadAsync(Fixture("timeline-basic.json"));
        Assert.Empty(t.Diagnostics);
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), t.DateRecordedUtc);   // string "1735732800"
        Assert.Equal(0L, t.StartTimeMs);
        Assert.Equal(900000L, t.EndTimeMs);
        Assert.Equal(["p1", "e0", "e1", "e2", "e3", "e4", "e5", "e6"], t.Entries.Select(e => e.Id));
    }

    [Fact]
    public async Task Entry_fields_are_mapped_with_utf8_text_and_types()
    {
        var t = await Timeline.ReadAsync(Fixture("timeline-basic.json"));
        var death = t.Entries.Single(e => e.Id == "e1");
        Assert.Equal(TimelineEntryType.Event, death.Type);
        Assert.Equal("You died \U0001F480", death.Title);
        Assert.Equal("Eliminated by élève", death.Description);
        Assert.Equal("cs2_death", death.Icon);
        Assert.Equal(2L, death.Priority);
        Assert.Equal(0L, death.DurationMs);
        Assert.True(death.PossibleClip);
        Assert.Equal(70000L, death.TimeMs);

        var dbl = t.Entries.Single(e => e.Id == "e3");
        Assert.Equal(8000L, dbl.DurationMs);

        Assert.Equal(TimelineEntryType.GameMode, t.Entries.Single(e => e.Id == "e0").Type);
        Assert.Equal("warmup", t.Entries.Single(e => e.Id == "e0").Mode);
        Assert.Equal("1234567890", t.Entries.Single(e => e.Id == "e4").Handle);
        Assert.Equal(TimelineEntryType.Screenshot, t.Entries.Single(e => e.Id == "e4").Type);
        Assert.Equal("MADE_UP_ACH", t.Entries.Single(e => e.Id == "e5").AchievementName);
        Assert.False(t.Entries.Single(e => e.Id == "e2").PossibleClip);
    }

    [Fact]
    public async Task Phase_tags_are_read_and_unknown_types_are_kept()
    {
        var t = await Timeline.ReadAsync(Fixture("timeline-basic.json"));
        var phase = t.Entries[0];
        Assert.Equal(TimelineEntryType.Phase, phase.Type);
        Assert.Equal([new TimelineTag("de_made_up", "Map", "map", 1), new TimelineTag("Casual", "Mode", null, 2)], phase.Tags);

        var weird = t.Entries.Single(e => e.Id == "e6");
        Assert.Equal(TimelineEntryType.Unknown, weird.Type);
        Assert.Equal("weird_new_type", weird.RawType);
    }

    [Fact]
    public async Task Lenient_file_accepts_strings_for_numbers_and_skips_bad_entries_with_diagnostics()
    {
        var t = await Timeline.ReadAsync(Fixture("timeline-lenient.json"));
        Assert.Equal(new DateTimeOffset(2025, 1, 1, 13, 0, 0, TimeSpan.Zero), t.DateRecordedUtc);   // number this time
        Assert.Equal(300000L, t.EndTimeMs);

        Assert.Equal(["17", "neg-duration", "bad-tag", "no-type"], t.Entries.Select(e => e.Id));
        var first = t.Entries[0];
        Assert.Equal(5000L, first.TimeMs);
        Assert.Equal(2500L, first.DurationMs);
        Assert.Equal(3L, first.Priority);
        Assert.True(first.PossibleClip);

        Assert.Equal(0L, t.Entries[1].DurationMs);                       // negative clamps to 0
        Assert.Equal(["ok"], t.Entries[2].Tags.Select(x => x.Name));     // bad tags skipped
        Assert.Equal(TimelineEntryType.Unknown, t.Entries[3].Type);

        Assert.Equal(5, t.Diagnostics.Count);   // 3 skipped entries + 2 bad tags
        Assert.Contains(t.Diagnostics, m => m.Contains("entries[1]") && m.Contains("skipped"));
        Assert.Contains(t.Diagnostics, m => m.Contains("entries[2]") && m.Contains("not an object"));
        Assert.Contains(t.Diagnostics, m => m.Contains("entries[3]") && m.Contains("skipped"));
        Assert.Contains(t.Diagnostics, m => m.Contains("entries[5]") && m.Contains("tag"));
    }

    [Fact]
    public void Invalid_json_and_wrong_shapes_never_throw()
    {
        Assert.NotEmpty(Timeline.Parse("{ not json").Diagnostics);
        Assert.NotEmpty(Timeline.Parse("[1,2,3]").Diagnostics);
        Assert.NotEmpty(Timeline.Parse("").Diagnostics);
        var r = Timeline.Parse("""{"daterecorded":"abc","entries":{"a":1}}""");
        Assert.Null(r.DateRecordedUtc);
        Assert.Empty(r.Entries);
        Assert.Equal(2, r.Diagnostics.Count);
        Assert.Empty(Timeline.Parse("""{"entries":null}""").Diagnostics);
    }

    [Fact]
    public void Utf8_bom_and_trailing_commas_are_tolerated()
    {
        var json = Encoding.UTF8.GetPreamble().Concat(Encoding.UTF8.GetBytes("""{"daterecorded":1735732800,"entries":[{"id":"a","time":1,},]}""")).ToArray();
        var t = Timeline.Parse(json);
        Assert.Empty(t.Diagnostics);
        Assert.Single(t.Entries);
    }

    [Fact]
    public async Task Missing_file_is_a_diagnostic_and_cancellation_propagates()
    {
        var t = await Timeline.ReadAsync(Path.Combine(AppContext.BaseDirectory, "nope.json"));
        Assert.Single(t.Diagnostics);
        Assert.Empty(t.Entries);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => Timeline.ReadAsync(Fixture("timeline-basic.json"), new CancellationToken(canceled: true)));
    }
}
