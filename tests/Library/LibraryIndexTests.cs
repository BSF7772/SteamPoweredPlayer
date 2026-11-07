// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class LibraryIndexTests
{
    private static readonly Guid TestId = Guid.Parse("11111111-2222-3333-4444-555555555555");
    private static readonly DateTimeOffset Gen = new(2025, 6, 1, 8, 30, 0, TimeSpan.Zero);

    internal static LibraryIndex Sample() => LibraryIndex.Create(TestId, Gen,
        [
            new GameEntry("730", "Test Game", false, new GameArt("art/730/cover.jpg", "art/730/hero.jpg", "art/730/heroBlur.jpg", "art/730/logo.png")),
            new GameEntry("10307921510433554432", "Test Shortcut éテ", true),
        ],
        [
            new RecordingEntry(RecordingKind.CacheSession, "730", new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero),
                "video/bg_730_20250101_120000", [new SegmentRun(1, 40), new SegmentRun(45, 60)], 3.0, 171.0, Live: true, Temporary: true,
                MarkerCount: 2, SizeBytes: 123456789),
            new RecordingEntry(RecordingKind.Clip, "10307921510433554432", new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero),
                "clips/clip_10307921510433554432_20251231_235959", [new SegmentRun(1, 10)], 3.0, 30.0,
                Title: "Test clip", MarkerCount: 0, ExactOffsetMs: 1250, SizeBytes: 5000),
            new RecordingEntry(RecordingKind.DownloadedClip, null, new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero),
                "clips/clip_downloaded_20250102_030405", [new SegmentRun(1, 3)], 3.0, 9.0, SizeBytes: 1),
        ]);

    internal static string GoldenPath(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", "golden", name);

    [Fact]
    public void Round_trip_preserves_everything()
    {
        var original = Sample();
        var parsed = LibraryIndex.Parse(original.ToUtf8());
        Assert.Equal(LibraryIndexStatus.Ok, parsed.Status);
        var back = parsed.Index!;
        Assert.Equal(original.SchemaVersion, back.SchemaVersion);
        Assert.Equal(original.LibraryId, back.LibraryId);
        Assert.Equal(original.GeneratedUtc, back.GeneratedUtc);
        Assert.Equal(original.Games, back.Games);
        Assert.Equal(original.Recordings.Count, back.Recordings.Count);
        for (var i = 0; i < original.Recordings.Count; i++)
        {
            var a = original.Recordings[i];
            var b = back.Recordings[i];
            Assert.Equal(a with { Runs = [] }, b with { Runs = [] });
            Assert.Equal(a.Runs, b.Runs);
        }
    }

    [Fact]
    public void Output_is_camel_case_utc_strings_and_run_pairs()
    {
        var json = Encoding.UTF8.GetString(Sample().ToUtf8());
        Assert.Contains("\"schemaVersion\": 1", json);
        Assert.Contains("\"gameId\": \"730\"", json);
        Assert.Contains("\"startUtc\": \"2025-01-01T12:00:00Z\"", json);
        Assert.Contains("\"generatedUtc\": \"2025-06-01T08:30:00Z\"", json);
        Assert.Contains("\"kind\": \"downloadedClip\"", json);
        Assert.Contains("éテ", json);          // not \u-escaped
        Assert.DoesNotContain("\r", json);
        Assert.DoesNotContain("\"title\": null", json);
    }

    [Fact]
    public void Offset_timestamps_are_normalised_to_utc()
    {
        var idx = Sample() with { GeneratedUtc = new DateTimeOffset(2025, 6, 1, 11, 30, 0, TimeSpan.FromHours(3)) };
        Assert.Contains("\"generatedUtc\": \"2025-06-01T08:30:00Z\"", Encoding.UTF8.GetString(idx.ToUtf8()));
    }

    [Fact]
    public void Golden_v1_parses_and_serialises_byte_stable()
    {
        var path = GoldenPath("library-v1.json");
        if (Environment.GetEnvironmentVariable("SPP_UPDATE_GOLDEN") == "1")
            File.WriteAllBytes(path, Sample().ToUtf8());

        var bytes = File.ReadAllBytes(path);
        var parsed = LibraryIndex.Parse(bytes);
        Assert.Equal(LibraryIndexStatus.Ok, parsed.Status);
        Assert.Equal(Sample().LibraryId, parsed.Index!.LibraryId);
        Assert.Equal(3, parsed.Index.Recordings.Count);
        Assert.Equal(bytes, parsed.Index.ToUtf8());
    }

    [Fact]
    public void Unknown_fields_from_a_newer_writer_are_ignored()
    {
        var parsed = LibraryIndex.Parse(File.ReadAllBytes(GoldenPath("library-v1-extra-fields.json")));
        Assert.Equal(LibraryIndexStatus.Ok, parsed.Status);
        Assert.Equal(1, parsed.SchemaVersion);
        Assert.Equal("Test Game", parsed.Index!.Games[0].Name);
        Assert.Equal(new SegmentRun(1, 5), parsed.Index.Recordings[0].Runs[0]);
        Assert.Equal(3.0, parsed.Index.Recordings[0].SegmentSeconds);   // default when absent
    }

    [Fact]
    public void Higher_schema_version_is_reported_not_crashed_on()
    {
        var parsed = LibraryIndex.Parse(File.ReadAllBytes(GoldenPath("library-v1-newer-schema.json")));
        Assert.Equal(LibraryIndexStatus.NewerSchema, parsed.Status);
        Assert.Equal(7, parsed.SchemaVersion);
        Assert.Null(parsed.Index);            // incompatible shape: nothing usable, but no exception
    }

    [Fact]
    public void Higher_schema_with_compatible_shape_still_yields_a_best_effort_index()
    {
        var json = Encoding.UTF8.GetString(Sample().ToUtf8()).Replace("\"schemaVersion\": 1", "\"schemaVersion\": 2");
        var parsed = LibraryIndex.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(LibraryIndexStatus.NewerSchema, parsed.Status);
        Assert.NotNull(parsed.Index);
    }

    [Theory]
    [InlineData("")]
    [InlineData("not json")]
    [InlineData("[]")]
    [InlineData("{}")]
    [InlineData("{\"schemaVersion\":0}")]
    [InlineData("{\"schemaVersion\":1,\"libraryId\":\"x\",\"generatedUtc\":\"bad\",\"games\":[],\"recordings\":[]}")]
    [InlineData("{\"schemaVersion\":1,\"libraryId\":\"x\",\"generatedUtc\":\"2025-01-01T00:00:00Z\",\"games\":[],\"recordings\":[{\"kind\":\"???\",\"startUtc\":\"2025-01-01T00:00:00Z\",\"relativeFolder\":\"a\",\"runs\":[]}]}")]
    [InlineData("{\"schemaVersion\":1,\"libraryId\":\"x\",\"generatedUtc\":\"2025-01-01T00:00:00Z\",\"games\":[],\"recordings\":[{\"kind\":\"clip\",\"startUtc\":\"2025-01-01T00:00:00Z\",\"relativeFolder\":\"a\",\"runs\":[[5,1]]}]}")]
    public void Corrupt_input_reports_corrupt_and_never_throws(string json)
    {
        var parsed = LibraryIndex.Parse(Encoding.UTF8.GetBytes(json));
        Assert.Equal(LibraryIndexStatus.Corrupt, parsed.Status);
        Assert.Null(parsed.Index);
    }

    [Fact]
    public void Large_index_is_small_and_fast_to_parse()
    {
        var start = new DateTimeOffset(2025, 1, 1, 0, 0, 0, TimeSpan.Zero);
        var games = Enumerable.Range(1, 12).Select(i => new GameEntry((1000 + i).ToString(CultureInfo.InvariantCulture), $"Test Game {i}", false,
            new GameArt($"art/{1000 + i}/cover.jpg", $"art/{1000 + i}/hero.jpg", $"art/{1000 + i}/heroBlur.jpg", $"art/{1000 + i}/logo.png"))).ToList();
        var recs = Enumerable.Range(0, 200).Select(i => new RecordingEntry(
            i % 3 == 0 ? RecordingKind.Clip : RecordingKind.CacheSession, (1001 + i % 12).ToString(CultureInfo.InvariantCulture),
            start.AddHours(i), $"video/bg_{1001 + i % 12}_{start.AddHours(i):yyyyMMdd_HHmmss}",
            [new SegmentRun(1, 100), new SegmentRun(120, 400), new SegmentRun(420, 900)], 3.0, 2400.0,
            Temporary: true, Title: i % 5 == 0 ? "Some title" : null, MarkerCount: i % 7, ExactOffsetMs: 1000 + i, SizeBytes: 4_000_000_000L + i)).ToList();
        var index = LibraryIndex.Create(TestId, Gen, games, recs);

        var bytes = index.ToUtf8();
        Assert.True(bytes.Length < 1_000_000, $"index is {bytes.Length} bytes");

        for (var i = 0; i < 20; i++) LibraryIndex.Parse(bytes);   // warm
        var times = new List<double>();
        for (var i = 0; i < 10; i++)
        {
            var sw = Stopwatch.StartNew();
            var r = LibraryIndex.Parse(bytes);
            sw.Stop();
            Assert.Equal(LibraryIndexStatus.Ok, r.Status);
            times.Add(sw.Elapsed.TotalMilliseconds);
        }
        times.Sort();
        Assert.True(times[times.Count / 2] < 50, $"median parse {times[times.Count / 2]:F2} ms ({bytes.Length} bytes)");
        Console.WriteLine($"[index-bench] 200 recordings: {bytes.Length} bytes, median parse {times[times.Count / 2]:F2} ms");
    }
}
