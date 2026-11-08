// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Export;

namespace SteamPoweredPlayer.Tests.Export;

public sealed class ExportFolderListingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spp-explist-" + Guid.NewGuid().ToString("N"));

    public ExportFolderListingTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    private string Make(string name, int bytes, DateTime? utc = null)
    {
        var p = Path.Combine(_dir, name);
        Directory.CreateDirectory(Path.GetDirectoryName(p)!);
        File.WriteAllBytes(p, new byte[bytes]);
        if (utc is { } t) File.SetLastWriteTimeUtc(p, t);
        return p;
    }

    [Fact]
    public void Missing_null_or_blank_folder_is_empty()
    {
        Assert.Empty(ExportFolderListing.List(null));
        Assert.Empty(ExportFolderListing.List("  "));
        Assert.Empty(ExportFolderListing.List(Path.Combine(_dir, "nope")));
    }

    [Fact]
    public void Lists_only_video_files_newest_first_with_size_and_time()
    {
        Make("old.mp4", 10, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        Make("new.MKV", 20, new DateTime(2026, 3, 1, 0, 0, 0, DateTimeKind.Utc));
        Make("mid.webm", 30, new DateTime(2026, 2, 1, 0, 0, 0, DateTimeKind.Utc));
        Make("shot.png", 5);
        Make("notes.txt", 5);

        var l = ExportFolderListing.List(_dir);
        Assert.Equal(["new.MKV", "mid.webm", "old.mp4"], l.Select(f => f.Name));
        Assert.Equal([20L, 30L, 10L], l.Select(f => f.SizeBytes));
        Assert.Equal(new DateTimeOffset(2026, 3, 1, 0, 0, 0, TimeSpan.Zero), l[0].ModifiedUtc);
    }

    [Fact]
    public void Working_files_and_folders_are_skipped()
    {
        Make(".spp-export-abc.mp4", 10);
        Make(".spp-export-abc/part.mp4", 10);
        Make("done.mp4", 10);
        Assert.Equal(["done.mp4"], ExportFolderListing.List(_dir).Select(f => f.Name));
    }

    [Fact]
    public void Matches_game_by_sanitised_prefix_ignoring_case()
    {
        Make("Half-Life  ·  Clip 2026-01-01 10-00-00.mp4", 1);
        Make("half-life 2026-01-01 10-00-00.mp4", 1);
        Make("Halfway 2026.mp4", 1);                           // prefix of the name but not of the word
        Make("Other 2026.mp4", 1);

        var files = ExportFolderListing.List(_dir);
        var hits = ExportFolderListing.ForGame(files, "Half-Life").Select(f => f.Name).Order().ToList();
        Assert.Contains("Half-Life  ·  Clip 2026-01-01 10-00-00.mp4", hits);
        Assert.Contains("half-life 2026-01-01 10-00-00.mp4", hits);
        Assert.DoesNotContain("Halfway 2026.mp4", hits);
        Assert.DoesNotContain("Other 2026.mp4", hits);
    }

    [Fact]
    public void Game_names_with_invalid_characters_match_their_sanitised_file_names()
    {
        var stem = ExportPaths.SafeStem("Left: Dead? 2");
        Make(stem + " 2026-01-01 10-00-00.mp4", 1);
        Assert.Single(ExportFolderListing.ForGame(ExportFolderListing.List(_dir), "Left: Dead? 2"));
    }

    [Fact]
    public void No_game_or_no_match_is_empty()
    {
        Make("A 1.mp4", 1);
        var files = ExportFolderListing.List(_dir);
        Assert.Empty(ExportFolderListing.ForGame(files, null));
        Assert.Empty(ExportFolderListing.ForGame(files, "Zed"));
    }
}
