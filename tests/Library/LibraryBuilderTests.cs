// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class LibraryBuilderTests
{
    [Fact]
    public async Task Builds_games_recordings_and_markers_from_a_synthetic_tree()
    {
        using var s = new SynthLibrary();
        s.AddBg("bg_730_20250101_120000", 1, 20);
        s.AddClip("clip_730_20250101_130000", "bg_730_20250101_130000", 1, 5);
        s.AddClip("clip_downloaded_20250102_100000", "bg_730_20250102_100000", 1, 2);
        Directory.CreateDirectory(Path.Combine(s.Root, "video", "not-a-recording"));
        var tl = Path.Combine(s.Root, "timelines");
        Directory.CreateDirectory(tl);
        // the timeline started 120 s before the session: the entry at 130 s lands at 10 s, the one at 5 s is before the media
        File.WriteAllText(Path.Combine(tl, "timeline_73020250101_115800.json"),
            "{\"daterecorded\":\"1735732680\",\"entries\":[{\"id\":\"a\",\"time\":130000,\"type\":\"event\",\"title\":\"Kill\"},{\"id\":\"b\",\"time\":5000,\"type\":\"event\",\"title\":\"Early\"}]}");

        var b = new LibraryBuilder(s.Root, s.Names, new ArtworkStore(s.Data), s.Clock, new ScanOptions());
        var result = await b.ScanAllAsync(null, Guid.NewGuid());

        Assert.Equal(3, result.Folders);
        Assert.Equal(3, result.Index.Recordings.Count);
        Assert.Equal("Test Game", Assert.Single(result.Index.Games).Name);
        var bg = result.Index.Recordings.Single(r => r.Kind == RecordingKind.CacheSession);
        Assert.True(bg.Temporary);
        Assert.Equal(60, bg.DurationSeconds);
        Assert.Equal(1, bg.MarkerCount);
        Assert.True(bg.SizeBytes > 0);
        var clip = result.Index.Recordings.Single(r => r.Kind == RecordingKind.Clip);
        Assert.False(clip.Temporary);
        Assert.Equal("clips/clip_730_20250101_130000", clip.RelativeFolder);
        var dl = result.Index.Recordings.Single(r => r.Kind == RecordingKind.DownloadedClip);
        Assert.Null(dl.GameId);
        Assert.Equal(["clips/clip_downloaded_20250102_100000", "clips/clip_730_20250101_130000", "video/bg_730_20250101_120000"],
            result.Index.Recordings.Select(r => r.RelativeFolder));          // newest first
        Assert.True(result.Index.ToUtf8().Length > 0);                       // serialises as library.json v1
    }

    [Fact]
    public async Task Warm_pass_with_the_cache_makes_no_head_reads_and_the_same_index()
    {
        using var s = new SynthLibrary();
        s.AddBg("bg_730_20250101_120000", 1, 10);
        s.AddClip("clip_730_20250101_130000", "bg_730_20250101_130000", 1, 5);
        var reads = 0;
        var scan = new ScanOptions { HeadReader = (p, ct) => { Interlocked.Increment(ref reads); return ScanHeads.ReadAsync(p, ct); } };
        var id = Guid.NewGuid();
        var cold = await new LibraryBuilder(s.Root, s.Names, null, s.Clock, scan).ScanAllAsync(null, id);
        Assert.Equal(30, reads);
        reads = 0;
        var warm = await new LibraryBuilder(s.Root, s.Names, null, s.Clock, scan).ScanAllAsync(cold.Cache, id);
        Assert.Equal(0, reads);
        Assert.Equal(cold.Index.Recordings.Count, warm.Index.Recordings.Count);
        Assert.True(LibraryService.IndexEquals(cold.Index, warm.Index));
    }

    [Fact]
    public void Folder_names_are_validated_per_parent()
    {
        using var s = new SynthLibrary();
        var b = new LibraryBuilder(s.Root);
        Assert.NotNull(b.FolderFor("video/bg_730_20250101_120000"));
        Assert.NotNull(b.FolderFor("clips/clip_730_20250101_120000"));
        Assert.Null(b.FolderFor("video/clip_730_20250101_120000"));
        Assert.Null(b.FolderFor("clips/bg_730_20250101_120000"));
        Assert.Null(b.FolderFor("video/../x"));
        Assert.Null(b.FolderFor("timelines/x"));
    }
}
