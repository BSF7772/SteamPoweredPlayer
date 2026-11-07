// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.RealLibrary;

/// <summary>Synthetic reproductions of shapes the first real-library run found (2026-11-07). No real data.</summary>
public sealed class RealWorldShapesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spp-shapes-" + Guid.NewGuid().ToString("N"));

    public RealWorldShapesTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch (IOException) { } }

    [Fact]
    public void Loginusers_without_MostRecent_picks_the_newest_timestamp()
    {
        var doc = Vdf.ParseText("""
            "users"
            {
                "76561197960266001" { "AccountName" "a" "AutoLogin" "1" "timestamp" "1700000000" }
                "76561197960266002" { "AccountName" "b" "AutoLogin" "0" "timestamp" "1700000500" }
            }
            """);
        Assert.Equal(new SteamUser(76561197960266002UL), SteamPaths.ParseActiveUser(doc));
    }

    [Fact]
    public void Loginusers_MostRecent_still_wins_over_timestamp()
    {
        var doc = Vdf.ParseText("""
            "users" { "76561197960266001" { "MostRecent" "1" "timestamp" "1" } "76561197960266002" { "timestamp" "9" } }
            """);
        Assert.Equal(new SteamUser(76561197960266001UL), SteamPaths.ParseActiveUser(doc));
    }

    [Theory]
    [InlineData("""{ "daterecorded": "1735732800", "starttime": "0", "endtime": "1000", "entries": {} }""")]
    [InlineData("""{ "daterecorded": "1735732800", "starttime": "0", "endtime": "1000" }""")]
    public void Timeline_without_events_is_empty_and_clean(string json)
    {
        var t = Timeline.Parse(json);
        Assert.Empty(t.Entries);
        Assert.Empty(t.Diagnostics);
    }

    [Fact]
    public void Timeline_entries_as_non_empty_object_is_still_reported() =>
        Assert.NotEmpty(Timeline.Parse("""{ "daterecorded": "1735732800", "entries": { "a": 1 } }""").Diagnostics);

    [Theory]
    [InlineData("bg_730_20250101_120000_0")]
    [InlineData("fg_730_20250101_120000")]
    public async Task Clip_media_folder_with_new_names_is_found_and_plays(string mediaName)
    {
        var media = Path.Combine(_dir, "clip_730_20250101_120500", "video", mediaName);
        Directory.CreateDirectory(media);
        byte[] box = [0, 0, 0, 0x18, (byte)'s', (byte)'t', (byte)'y', (byte)'p', 1, 2, 3, 4];
        foreach (var f in new[] { "init-stream0.m4s", "init-stream1.m4s", "chunk-stream0-00001.m4s", "chunk-stream1-00001.m4s" })
            await File.WriteAllBytesAsync(Path.Combine(media, f), box);

        Assert.Equal(Path.GetFullPath(media), Scanner.FindClipMediaFolder(Path.GetDirectoryName(Path.GetDirectoryName(media))!));
        var r = await Scanner.ScanClipAsync(Path.GetDirectoryName(Path.GetDirectoryName(media))!);
        Assert.Equal(ScanStatus.Playable, r.Status);
        Assert.False(r.Media!.Temporary);
    }

    [Fact]
    public async Task Clip_without_media_folder_is_not_playable_rather_than_gone()
    {
        var clip = Path.Combine(_dir, "clip_730_20250101_120500");
        Directory.CreateDirectory(Path.Combine(clip, "video"));
        var r = await Scanner.ScanClipAsync(clip);
        Assert.Equal(ScanStatus.NotPlayable, r.Status);
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.MissingMediaFolder);
        Assert.Equal(ScanStatus.Gone, (await Scanner.ScanClipAsync(Path.Combine(_dir, "missing"))).Status);
    }
}
