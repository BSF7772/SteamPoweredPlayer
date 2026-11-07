// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Steam;

public sealed class FolderNamesTests
{
    [Fact]
    public void Background_session_parses_as_utc_cache_session()
    {
        var r = FolderNames.ParseRecording("bg_730_20250101_120000");
        Assert.Equal(new RecordingName(RecordingKind.CacheSession, 730UL, new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero)), r);
        Assert.Equal(TimeSpan.Zero, r!.StartUtc.Offset);
    }

    [Fact]
    public void Clip_with_shortcut_game_id_parses()
    {
        var r = FolderNames.ParseRecording("clip_10307921510433554432_20251231_235959");
        Assert.Equal(RecordingKind.Clip, r!.Kind);
        Assert.Equal(10307921510433554432UL, r.GameId);
        Assert.Equal(new DateTimeOffset(2025, 12, 31, 23, 59, 59, TimeSpan.Zero), r.StartUtc);
    }

    [Fact]
    public void Downloaded_clip_has_no_game_id()
    {
        var r = FolderNames.ParseRecording("clip_downloaded_20250102_030405");
        Assert.Equal(new RecordingName(RecordingKind.DownloadedClip, null, new DateTimeOffset(2025, 1, 2, 3, 4, 5, TimeSpan.Zero)), r);
    }

    [Theory]
    [InlineData("bg_730_20250101_1200")]        // short time
    [InlineData("bg_730_20251301_120000")]      // month 13
    [InlineData("bg_730_20250101_120000_x")]
    [InlineData("rec_730_20250101_120000")]
    [InlineData("bg_99999999999999999999999_20250101_120000")] // > ulong
    [InlineData("BG_730_20250101_120000")]
    [InlineData("")]
    public void Unknown_layouts_return_null(string name) => Assert.Null(FolderNames.ParseRecording(name));

    [Fact]
    public void Timeline_without_separator_splits_game_id_and_date()
    {
        var t = FolderNames.ParseTimeline("timeline_73020250101_120000.json");
        Assert.Equal(new TimelineName(730UL, new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero)), t);
    }

    [Fact]
    public void Timeline_rejects_other_files() => Assert.Null(FolderNames.ParseTimeline("timeline_730_20250101_120000.json"));

    [Theory]
    [InlineData("chunk-stream0-01840.m4s", 0, 1840, false)]
    [InlineData("chunk-stream1-00001.m4s", 1, 1, false)]
    [InlineData("chunk-stream0-123456.m4s", 0, 123456, false)]
    [InlineData("chunk-stream1-00007.m4s.tmp", 1, 7, true)]
    public void Chunk_names_parse(string name, int stream, int number, bool tmp) =>
        Assert.Equal(new ChunkName(stream, number, tmp), FolderNames.ParseChunk(name));

    [Theory]
    [InlineData("chunk-stream2-00001.m4s")]
    [InlineData("init-stream0.m4s")]
    [InlineData("chunk-stream0-00001.mp4")]
    public void Non_chunks_return_null(string name) => Assert.Null(FolderNames.ParseChunk(name));

    [Theory]
    [InlineData("bg_730_20250101_120000", false, null)]
    [InlineData("bg_730_20250101_120000_0", false, 0)]
    [InlineData("fg_730_20250101_120000", true, null)]
    public void Clip_media_folders_parse(string name, bool foreground, int? index)
    {
        var m = FolderNames.ParseMediaFolder(name);
        Assert.Equal(new MediaFolderName(foreground, 730UL, new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), index), m);
    }

    [Theory]
    [InlineData("clip_730_20250101_120000")]
    [InlineData("bg_730_20250101_120000_")]
    [InlineData("xg_730_20250101_120000")]
    public void Other_media_folder_names_return_null(string name) => Assert.Null(FolderNames.ParseMediaFolder(name));

    [Fact]
    public void Chunk_file_round_trips() => Assert.Equal("chunk-stream0-01840.m4s", FolderNames.ChunkFile(0, 1840));
}
