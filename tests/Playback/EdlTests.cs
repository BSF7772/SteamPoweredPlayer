// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Tests.Playback;

[Collection(BudgetCollection.Name)]
public sealed class EdlTests
{
    private const string Folder = "C:/rec/bg_730_20250101_120000";

    private static SessionMedia Media(string folder, params (int First, int Last)[] runs) =>
        new(folder, [.. runs.Select(r => new SegmentRun(r.First, r.Last))], 3.0, Live: false, Temporary: true);

    private static List<int> Numbers(params (int First, int Last)[] ranges) =>
        [.. ranges.SelectMany(r => Enumerable.Range(r.First, r.Last - r.First + 1))];

    private static string GoldenDir => Path.Combine(AppContext.BaseDirectory, "fixtures", "golden");
    private static string Lf(string s) => s.Replace("\r\n", "\n");

    private static (int, int)[] Ranges(JsonElement e) => [.. e.EnumerateArray().Select(p => (p[0].GetInt32(), p[1].GetInt32()))];

    // ---- golden files -----------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> GoldenCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDir, "edl-cases.json")));
        foreach (var c in doc.RootElement.GetProperty("cases").EnumerateArray())
            yield return [c.GetProperty("name").GetString()!];
    }

    [Theory]
    [MemberData(nameof(GoldenCases))]
    public void Golden_edl_matches_byte_for_byte(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDir, "edl-cases.json")));
        var c = doc.RootElement.GetProperty("cases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var chunks = Numbers(Ranges(c.GetProperty("chunks")));
        var media = new SessionMedia(c.GetProperty("folder").GetString()!, ScanAnalysis.Runs(chunks), c.GetProperty("segmentSeconds").GetDouble(), false, true);

        var expected = Lf(File.ReadAllText(Path.Combine(GoldenDir, c.GetProperty("expected").GetString()!), new UTF8Encoding(false)));
        Assert.Equal(expected, Edl.Build(media, chunks, p => p));
    }

    // ---- derivation: the EDL axis equals the tfdt axis --------------------------------------------------------------

    /// <summary>Parses the video track back: returns (chunk number, length) pairs.</summary>
    private static List<(int Chunk, double Length)> VideoParts(string edl)
    {
        var parts = new List<(int, double)>();
        foreach (var line in edl.Split('\n'))
        {
            if (line == "!new_stream") break;
            var i = line.IndexOf("chunk-stream0-", StringComparison.Ordinal);
            if (i < 0) continue;
            var n = int.Parse(line.AsSpan(i + 14, 5));
            var len = double.Parse(line[(line.IndexOf("length=", StringComparison.Ordinal) + 7)..], System.Globalization.CultureInfo.InvariantCulture);
            parts.Add((n, len));
        }
        return parts;
    }

    [Theory]
    [InlineData(1840, 1842, 0, 0)]
    [InlineData(1, 5, 0, 0)]
    public void Parts_tile_the_tfdt_axis_without_holes(int a, int b, int c, int d)
    {
        var chunks = Numbers((a, b), (c, d)).Where(x => x > 0).ToList();
        AssertTiling(chunks);
    }

    [Fact]
    public void Parts_tile_the_axis_with_gaps_and_a_first_part_followed_by_a_gap()
    {
        AssertTiling(Numbers((5, 5), (9, 10), (14, 14), (20, 25)));
        AssertTiling(Numbers((1, 1), (3, 3)));
        AssertTiling(Numbers((2, 2)));
    }

    private static void AssertTiling(List<int> chunks)
    {
        var media = new SessionMedia(Folder, ScanAnalysis.Runs(chunks), 3.0, false, true);
        var parts = VideoParts(Edl.Build(media, chunks, p => p));
        Assert.Equal(chunks, parts.Select(p => p.Chunk));
        // Part k starts where the previous one ended; the first starts at 0; each chunk's real start (n-1)*3 lies inside
        // its part, and the sum reaches the end of the last chunk, n_last * 3.
        double t = 0;
        foreach (var (n, len) in parts)
        {
            var chunkStart = (n - 1) * 3.0;
            Assert.True(t <= chunkStart && chunkStart < t + len, $"chunk {n} start {chunkStart} outside part [{t}, {t + len})");
            t += len;
        }
        Assert.Equal(chunks[^1] * 3.0, t);
    }

    [Fact]
    public void Audio_track_has_the_same_lengths_with_stream_one()
    {
        var chunks = Numbers((5, 5), (9, 10));
        var edl = Edl.Build(Media(Folder, (5, 5), (9, 10)), chunks, p => p);
        var sections = edl.Split("!new_stream\n");
        Assert.Equal(2, sections.Length);
        Assert.Equal(sections[0].Replace("stream0", "stream1").Split('\n', 3)[2], sections[1]);
        Assert.StartsWith("# mpv EDL v0\n!no_chapters\n!mp4_dash,init=", edl);
    }

    [Fact]
    public void Url_mapper_decides_what_mpv_opens()
    {
        var chunks = Numbers((3, 4));
        var edl = Edl.Build(Media(Folder, (3, 4)), chunks, p => "http://127.0.0.1:9/" + Path.GetFileName(p));
        Assert.Contains("%42%http://127.0.0.1:9/chunk-stream0-00003.m4s,length=9", edl);
        Assert.Contains("init=%35%http://127.0.0.1:9/init-stream1.m4s\n", edl);
    }

    [Fact]
    public void Windows_backslashes_in_the_folder_are_written_as_slashes()
    {
        var edl = Edl.Build(Media(@"C:\rec\bg_730_20250101_120000\", (1, 1)), [1], p => p);
        Assert.DoesNotContain('\\', edl);
        Assert.Contains("%53%C:/rec/bg_730_20250101_120000/chunk-stream0-00001.m4s,length=3\n", edl);
    }

    [Fact]
    public void Escape_counts_utf8_bytes_not_characters()
    {
        Assert.Equal("%5%abcde", Edl.Escape("abcde"));
        Assert.Equal("%5%Gäme", Edl.Escape("Gäme"));                       // ä = 2 bytes
        Assert.Equal("%6%🎮ab", Edl.Escape("🎮ab"));                        // emoji = 4 bytes (2 UTF-16 units)
        Assert.Equal("%8%a,b=%c;d", Edl.Escape("a,b=%c;d"));               // special characters are carried by the count
        Assert.Equal("%0%", Edl.Escape(""));
    }

    [Fact]
    public void Lengths_use_the_invariant_culture_and_fractional_segments()
    {
        var chunks = Numbers((2, 3));
        var media = new SessionMedia(Folder, ScanAnalysis.Runs(chunks), 2.5, false, true);
        var old = System.Globalization.CultureInfo.CurrentCulture;
        try
        {
            var comma = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            System.Globalization.CultureInfo.CurrentCulture = comma;
            var edl = Edl.Build(media, chunks, p => p);
            Assert.Contains("00002.m4s,length=5\n", edl);       // 2 * 2.5
            Assert.Contains("00003.m4s,length=2.5\n", edl);
        }
        finally { System.Globalization.CultureInfo.CurrentCulture = old; }
    }

    [Fact]
    public void Empty_or_unsorted_windows_are_rejected()
    {
        var m = Media(Folder, (1, 5));
        Assert.Throws<ArgumentException>(() => Edl.Build(m, [], p => p));
        Assert.Throws<ArgumentException>(() => Edl.Build(m, [3, 2], p => p));
        Assert.Throws<ArgumentException>(() => Edl.Build(m, [2, 2], p => p));
    }

    [Fact]
    public void Building_2400_segments_takes_under_5_ms()
    {
        var chunks = Numbers((1, 2400));
        var media = Media(Folder, (1, 2400));
        var s = "";
        for (var i = 0; i < 30; i++) s = Edl.Build(media, chunks, p => p);   // warm
        var sw = Stopwatch.StartNew();
        const int loops = 20;
        for (var i = 0; i < loops; i++) s = Edl.Build(media, chunks, p => p);
        var perCall = sw.Elapsed.TotalMilliseconds / loops;
        Assert.Equal(2 * 2400, s.Split('\n').Count(l => l.Contains(",length=")));
        Assert.True(perCall < 5, $"EDL build took {perCall:F2} ms");
    }

    // ---- window -----------------------------------------------------------------------------------------------------

    public static IEnumerable<object[]> WindowCases()
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDir, "edl-cases.json")));
        foreach (var c in doc.RootElement.GetProperty("windowCases").EnumerateArray())
            yield return [c.GetProperty("name").GetString()!];
    }

    [Theory]
    [MemberData(nameof(WindowCases))]
    public void Golden_window_cases(string name)
    {
        using var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(GoldenDir, "edl-cases.json")));
        var c = doc.RootElement.GetProperty("windowCases").EnumerateArray().Single(x => x.GetProperty("name").GetString() == name);
        var media = Media(Folder, Ranges(c.GetProperty("runs")));
        var got = EdlWindow.Window(media, c.GetProperty("position").GetDouble(), c.GetProperty("count").GetInt32());
        Assert.Equal(Numbers(Ranges(c.GetProperty("expected"))), got);
    }

    [Fact]
    public void Default_window_is_60_chunks()
    {
        var w = EdlWindow.Window(Media(Folder, (1, 1000)), 1500);
        Assert.Equal(60, w.Count);
        Assert.Equal(60, EdlWindow.DefaultCount);
    }

    [Fact]
    public void Window_of_empty_media_is_empty()
    {
        Assert.Empty(EdlWindow.Window(new SessionMedia(Folder, [], 3.0, false, true), 10));
    }

    [Theory]
    [InlineData(-5.0)]
    [InlineData(double.NaN)]
    [InlineData(1e12)]
    [InlineData(double.PositiveInfinity)]
    public void Odd_positions_are_clamped(double pos)
    {
        var w = EdlWindow.Window(Media(Folder, (1, 100)), pos, 10);
        Assert.Equal(10, w.Count);
        Assert.InRange(w[0], 1, 91);
    }

    [Fact]
    public void Chunk_boundaries_pick_the_chunk_that_starts_there()
    {
        // 3.0 s is the start of chunk 2: chunk 2 gets look-behind chunks before it.
        var w = EdlWindow.Window(Media(Folder, (1, 100)), 3.0, 8);
        Assert.Equal(Enumerable.Range(1, 8), w);                       // start clamps at 1 (look-behind 2 from index 1)
        var w2 = EdlWindow.Window(Media(Folder, (1, 100)), 150.0, 8);   // chunk 51, index 50, behind 2 -> chunks 49..56
        Assert.Equal(Enumerable.Range(49, 8), w2);
    }

    [Fact]
    public void Window_is_always_a_valid_edl_input()
    {
        var media = Media(Folder, (10, 20), (40, 60), (100, 140));
        for (var pos = 0.0; pos < 450; pos += 7.3)
        {
            var w = EdlWindow.Window(media, pos, 25);
            Assert.All(w, n => Assert.True(media.Contains(n)));
            Assert.Equal(w.Order(), w);
            Assert.Equal(w.Count, w.Distinct().Count());
            _ = Edl.Build(media, w, p => p);
        }
    }

    [Fact]
    public void Invalid_count_throws() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => EdlWindow.Window(Media(Folder, (1, 5)), 0, 0));

    // ---- reload policy ----------------------------------------------------------------------------------------------

    private static readonly SessionMedia Long = Media(Folder, (1, 200));
    private static readonly IReadOnlyList<int> Mid = Numbers((86, 145));          // window in the middle of the session

    [Fact]
    public void Playhead_inside_the_window_and_far_from_its_end_keeps_playing()
    {
        Assert.Equal(ReloadReason.None, EdlWindow.ShouldReload(Mid, Long, 300.0, 5.0));          // chunk 101
        Assert.Equal(ReloadReason.None, EdlWindow.ShouldReload(Mid, Long, (86 - 1) * 3.0, 0));    // window start is fine
    }

    [Fact]
    public void Playhead_near_the_window_end_rebuilds()
    {
        var edge = 145 - EdlWindow.ReloadMarginChunks;                                          // chunk 135 is exactly at the margin
        Assert.Equal(ReloadReason.NearWindowEnd, EdlWindow.ShouldReload(Mid, Long, (edge - 1) * 3.0, 1));
        Assert.Equal(ReloadReason.None, EdlWindow.ShouldReload(Mid, Long, (edge - 2) * 3.0, 1));
    }

    [Fact]
    public void Window_that_reaches_the_session_end_cannot_be_extended_so_it_does_not_rebuild()
    {
        var tail = Numbers((141, 200));
        Assert.Equal(ReloadReason.None, EdlWindow.ShouldReload(tail, Long, 199 * 3.0, 1));
        Assert.Equal(ReloadReason.None, EdlWindow.ShouldReload(tail, Long, 5000.0, 1));          // past the end (live follower handles growth)
    }

    [Fact]
    public void Live_growth_makes_the_window_end_extendable_again()
    {
        var tail = Numbers((141, 200));
        var grown = Media(Folder, (1, 230));
        Assert.Equal(ReloadReason.NearWindowEnd, EdlWindow.ShouldReload(tail, grown, 199 * 3.0, 1));
    }

    [Fact]
    public void Playhead_outside_the_window_rebuilds()
    {
        Assert.Equal(ReloadReason.OutsideWindow, EdlWindow.ShouldReload(Mid, Long, 30.0, 1));      // chunk 11, before
        Assert.Equal(ReloadReason.OutsideWindow, EdlWindow.ShouldReload(Mid, Long, 500.0, 1));     // chunk 167, after
    }

    [Fact]
    public void Lead_part_before_the_first_chunk_is_inside_a_window_that_starts_at_the_session_start()
    {
        var head = Numbers((1840, 1899));
        var trimmed = Media(Folder, (1840, 1900));
        Assert.Equal(ReloadReason.None, EdlWindow.ShouldReload(head, trimmed, 12.0, 1));           // in the lead part (axis before chunk 1840)
    }

    [Fact]
    public void Window_whose_head_was_trimmed_away_is_stale()
    {
        var trimmed = Media(Folder, (100, 200));                                               // Steam removed chunks 1..99
        Assert.Equal(ReloadReason.WindowStale, EdlWindow.ShouldReload(Mid, trimmed, 300.0, 1));
    }

    [Fact]
    public void Periodic_reload_bounds_ram_after_many_media_seconds()
    {
        Assert.Equal(ReloadReason.None, EdlWindow.ShouldReload(Mid, Long, 300.0, EdlWindow.PeriodicReloadMediaSeconds - 1));
        Assert.Equal(ReloadReason.Periodic, EdlWindow.ShouldReload(Mid, Long, 300.0, EdlWindow.PeriodicReloadMediaSeconds));
    }

    [Fact]
    public void Empty_window_or_media_asks_for_a_rebuild_only_if_there_is_something_to_play()
    {
        Assert.Equal(ReloadReason.OutsideWindow, EdlWindow.ShouldReload([], Long, 10.0, 0));
        Assert.Equal(ReloadReason.None, EdlWindow.ShouldReload([], new SessionMedia(Folder, [], 3.0, false, true), 10.0, 0));
    }

    [Fact]
    public void Reasons_have_a_priority_outside_then_stale_then_near_end_then_periodic()
    {
        var big = EdlWindow.PeriodicReloadMediaSeconds + 1;
        Assert.Equal(ReloadReason.OutsideWindow, EdlWindow.ShouldReload(Mid, Long, 30.0, big));
        Assert.Equal(ReloadReason.NearWindowEnd, EdlWindow.ShouldReload(Mid, Long, 144 * 3.0, big));
    }
}
