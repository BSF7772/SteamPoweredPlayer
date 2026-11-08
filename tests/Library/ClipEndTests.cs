// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Tests.Fakes;

namespace SteamPoweredPlayer.Tests.Library;

/// <summary>
/// A saved clip keeps whole 3 s chunks around the part Steam meant: its <c>session.mpd</c> has <c>Period start</c> and
/// <c>mediaPresentationDuration</c>, and the chunks run past <c>start + duration</c> by 0..3 s (196 of 196 real clips). The player and every
/// export must stop at that intended end. Cache sessions have no such end.
/// </summary>
public sealed class ClipEndTests
{
    // The real shape (numbers redacted): 4 chunks from tfdt 1533 s, Steam's clip is [1534.317, 1544.409] of the 1533..1545 span.
    private const double Shift = 1533;

    private static string Mpd(string start = "PT25M34.317S", string duration = "PT10.092S", string type = "static") =>
        $"<?xml version=\"1.0\" encoding=\"utf-8\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"{type}\" mediaPresentationDuration=\"{duration}\">\n"
        + $"    <Period id=\"0\" start=\"{start}\">\n        <AdaptationSet id=\"0\" contentType=\"video\"/>\n    </Period>\n</MPD>";

    private static SessionMedia Clip(double? end = null, int last = 4, double shift = Shift) =>
        new("C:/rec/clip_730_20250101_120000/video/bg_730_20250101_115000", [new SegmentRun(1, last)], 3.0, false, false, shift, EndLimitSeconds: end);

    // ---- the rule --------------------------------------------------------------------------------------------------

    [Fact]
    public void End_is_period_start_plus_duration()
    {
        Assert.Equal(1544.409, ClipEnd.FromManifest(Mpd(), 1545, 3.0)!.Value, 6);
    }

    [Theory]
    [InlineData("PT1H2M3.5S", 3723.5)]
    [InlineData("PT25M34.317S", 1534.317)]
    [InlineData("PT0.5S", 0.5)]
    [InlineData("PT7M", 420)]
    public void Durations_in_iso_8601_are_read(string text, double seconds) => Assert.Equal(seconds, ClipEnd.ParseDuration(text));

    [Theory]
    [InlineData("")]
    [InlineData("10.092")]
    [InlineData("PT")]
    [InlineData("P1D")]
    [InlineData("PT-5S")]
    [InlineData("PTxS")]
    public void Anything_else_is_not_a_duration(string text) => Assert.Null(ClipEnd.ParseDuration(text));

    [Fact]
    public void The_end_must_leave_less_than_a_chunk_of_tail()
    {
        // tail = chunk end − end: 0.591 here. A tail of a whole chunk or more means the manifest and the chunks disagree: no trim.
        Assert.NotNull(ClipEnd.FromManifest(Mpd(), 1545, 3.0));
        Assert.Null(ClipEnd.FromManifest(Mpd(), 1548, 3.0));           // a chunk the manifest doesn't know about (3.59 s tail)
        Assert.NotNull(ClipEnd.FromManifest(Mpd(), 1547.45, 3.0));     // 3.04 s: the longest real tail, 3.027 s, plus jitter
    }

    [Fact]
    public void An_end_at_or_past_the_last_chunk_is_not_a_trim()
    {
        Assert.Null(ClipEnd.FromManifest(Mpd(), 1544.409, 3.0));
        Assert.Null(ClipEnd.FromManifest(Mpd(), 1544.0, 3.0));        // the last chunk on disk is shorter than the manifest says
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("\0\0\0\0\0\0\0\0")]                                  // a crashed recording
    [InlineData("<MPD type=\"static\"></MPD>")]                       // nothing to read
    public void Unreadable_manifests_give_no_end(string? mpd) => Assert.Null(ClipEnd.FromManifest(mpd, 1545, 3.0));

    [Fact]
    public void A_live_manifest_gives_no_end() =>
        Assert.Null(ClipEnd.FromManifest(Mpd(type: "dynamic"), 1545, 3.0));

    [Fact]
    public void Missing_duration_or_start_gives_no_end()
    {
        Assert.Null(ClipEnd.FromManifest(Mpd().Replace(" mediaPresentationDuration=\"PT10.092S\"", ""), 1545, 3.0));
        Assert.Null(ClipEnd.FromManifest(Mpd().Replace(" start=\"PT25M34.317S\"", ""), 1545, 3.0));
        Assert.Null(ClipEnd.FromManifest(Mpd(duration: "PT0.0S"), 1545, 3.0));       // a trimmed cache session says this
    }

    [Fact]
    public void Apply_sets_the_limit_on_a_clip_and_leaves_a_missing_manifest_alone()
    {
        var media = Clip();
        Assert.Equal(1544.409, ClipEnd.Apply(media, Mpd()).EndLimitSeconds!.Value, 6);
        Assert.Same(media, ClipEnd.Apply(media, null));
        var empty = media with { Runs = [] };
        Assert.Same(empty, ClipEnd.Apply(empty, Mpd()));
    }

    // ---- SessionMedia ----------------------------------------------------------------------------------------------

    [Fact]
    public void Without_a_limit_the_end_is_the_last_chunks_end()
    {
        var m = Clip();
        Assert.Equal(1545, m.EndSeconds);
        Assert.Equal(12, m.DurationSeconds);
    }

    [Fact]
    public void With_a_limit_the_end_and_the_length_stop_there()
    {
        var m = Clip(1544.409);
        Assert.Equal(1544.409, m.EndSeconds, 9);
        Assert.Equal(11.409, m.DurationSeconds, 9);
    }

    [Fact]
    public void A_limit_at_or_past_the_last_chunk_counts_for_nothing()
    {
        Assert.Equal(1545, Clip(1546).EndSeconds);
        Assert.Equal(12, Clip(1546).DurationSeconds);
        Assert.Equal(0, (Clip() with { Runs = [] }).EndSeconds);
    }

    [Fact]
    public void Only_the_last_chunk_is_shortened_and_gaps_still_do_not_count()
    {
        var m = Clip(1544.409) with { Runs = [new SegmentRun(1, 1), new SegmentRun(3, 4)] };
        Assert.Equal(9 - 0.591, m.DurationSeconds, 9);
    }

    // ---- exports ---------------------------------------------------------------------------------------------------

    [Fact]
    public void A_whole_clip_export_ends_at_the_intended_end()
    {
        var plan = ExportPlan.Whole(Clip(1544.409))!;
        Assert.Equal(1544.409, plan.End, 9);
        Assert.Equal(1545, plan.SnappedEnd);                           // the chunk still has to be read
        Assert.Equal((1, 4), (plan.FirstChunk, plan.LastChunk));
    }

    [Fact]
    public void A_range_past_the_end_is_cut_to_it_and_one_beyond_it_has_no_plan()
    {
        var m = Clip(1544.409);
        Assert.Equal(1544.409, ExportPlan.For(m, 1540, 1545)!.End, 9);
        Assert.Equal(1543, ExportPlan.For(m, 1540, 1543)!.End);
        Assert.Null(ExportPlan.For(m, 1544.5, 1545));
        Assert.Null(ExportPlan.For(m, 1544.409, 1545));
    }

    [Fact]
    public void A_range_starting_in_the_dropped_tail_does_not_pull_in_its_chunk()
    {
        // the tail 1544.409..1545 is all of chunk 4's range that matters: a range ending at the limit must not read past chunk 4
        var p = ExportPlan.For(Clip(1541.3), 1534, 1545)!;             // limit inside chunk 3 (1539..1542)
        Assert.Equal((1, 3), (p.FirstChunk, p.LastChunk));
        Assert.Equal(1541.3, p.End, 9);
    }

    [Fact]
    public void Highlights_never_reach_past_the_end()
    {
        var spans = ExportSpans.Highlights(Clip(1544.409), [1543.5, 1544.8], before: 1, after: 3);
        Assert.Single(spans);
        Assert.Equal(1544.409, spans[0].End, 9);
    }

    [Fact]
    public void Lossless_end_of_a_trimmed_plan_is_exact_not_padded()
    {
        var plan = ExportPlan.Whole(Clip(1544.409))!;
        Assert.Equal(1544.409, ExportService.LosslessEnd(plan), 9);
        var (_, end) = ExportService.OutputTrim(plan, 1533.01, 11.99);
        Assert.Equal(1544.409 - 1533.01, end!.Value, 6);               // a re-encode cuts there too instead of running to the file's end
    }

    // ---- playback --------------------------------------------------------------------------------------------------

    [Fact]
    public async Task The_session_reports_the_trimmed_length_and_loads_with_the_end()
    {
        var p = new FakeMpvPlayer();
        await using var s = new PlaybackSession(p);
        var media = Clip(1544.409);
        await s.OpenAsync(media, play: false);
        Assert.Equal(1544.409, s.MediaEndSeconds, 9);
        Assert.Equal(11.409, s.UiDuration, 9);
        Assert.Equal(11.409, s.PlayableSeconds, 9);
        Assert.All(p.Loads, l => Assert.Equal(1544.409, l.End!.Value, 9));
    }

    [Fact]
    public async Task A_clip_without_a_limit_and_a_cache_session_load_without_an_end()
    {
        var p = new FakeMpvPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(Clip(), play: false);
        Assert.Equal(1545, s.MediaEndSeconds);
        Assert.All(p.Loads, l => Assert.Null(l.End));
    }

    [Fact]
    public async Task Seeks_past_the_end_stop_before_it_and_reload_with_the_end()
    {
        var p = new FakeMpvPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(Clip(1544.409), play: false);
        await s.SeekAsync(1544.9);
        Assert.True(p.Loads[^1].Start < 1544.409);
        Assert.Equal(1544.409, p.Loads[^1].End!.Value, 9);
        Assert.True(PlaybackSession.Snap(Clip(1544.409), 5000) < 1544.409);
    }

    [Fact]
    public void Edl_is_unchanged_the_end_is_mpvs_job()
    {
        Assert.Equal(Edl.Build(Clip(), [1, 2, 3, 4], u => u), Edl.Build(Clip(1544.409), [1, 2, 3, 4], u => u));
    }

    // ---- scanning --------------------------------------------------------------------------------------------------

    private static (string Clip, string Media) FakeClip(SynthLibrary lib, string mpdText)
    {
        var clip = lib.AddClip("clip_730_20250101_120000", "bg_730_20250101_115900", 1, 4);
        var media = Path.Combine(clip, "video", "bg_730_20250101_115900");
        File.WriteAllText(Path.Combine(media, "session.mpd"), mpdText);
        return (clip, media);
    }

    [Fact]
    public async Task Scanning_a_clip_reads_its_end_from_the_manifest()
    {
        using var lib = new SynthLibrary();
        var (clip, _) = FakeClip(lib, Mpd(start: "PT1.3S", duration: "PT10.092S"));       // no real tfdt in the fake heads: axis 0
        var r = await Scanner.ScanClipAsync(clip);
        Assert.Equal(11.392, r.Media!.EndLimitSeconds!.Value, 6);
        Assert.Equal(11.392, r.Media.EndSeconds, 6);
        Assert.Equal(11.392, r.Media.DurationSeconds, 6);
    }

    [Fact]
    public async Task Scanning_the_same_folder_as_a_cache_session_never_trims()
    {
        using var lib = new SynthLibrary();
        var (_, media) = FakeClip(lib, Mpd(start: "PT1.3S", duration: "PT10.092S"));
        var r = await Scanner.ScanAsync(media, new ScanOptions { Temporary = true });
        Assert.Null(r.Media!.EndLimitSeconds);
        Assert.Equal(12, r.Media.DurationSeconds);
    }

    [Fact]
    public async Task A_clip_with_a_zero_filled_manifest_plays_to_its_last_chunk()
    {
        using var lib = new SynthLibrary();
        var (clip, media) = FakeClip(lib, "");
        File.WriteAllBytes(Path.Combine(media, "session.mpd"), new byte[300]);
        var r = await Scanner.ScanClipAsync(clip);
        Assert.Null(r.Media!.EndLimitSeconds);
        Assert.Equal(12, r.Media.DurationSeconds);
    }

    [Fact]
    public void A_changed_limit_is_a_changed_media()
    {
        Assert.False(LibraryService.SameMedia(Clip(), Clip(1544.409)));
        Assert.True(LibraryService.SameMedia(Clip(1544.409), Clip(1544.409)));
    }
}
