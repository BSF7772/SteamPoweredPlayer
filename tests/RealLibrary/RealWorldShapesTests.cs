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

    private static async Task WriteMediaAsync(string media)
    {
        Directory.CreateDirectory(media);
        byte[] box = [0, 0, 0, 0x18, (byte)'s', (byte)'t', (byte)'y', (byte)'p', 1, 2, 3, 4];
        foreach (var f in new[] { "init-stream0.m4s", "init-stream1.m4s", "chunk-stream0-00001.m4s", "chunk-stream1-00001.m4s" })
            await File.WriteAllBytesAsync(Path.Combine(media, f), box);
    }

    /// <summary>clip.pb: 1 timelines {1 id, 5 recordings {1 recording_id, 10 zero offset}}; Steam's CGameRecordingClipFile.</summary>
    private static byte[] ClipPbNaming(params (string Folder, long Offset)[] recordings)
    {
        using var tl = new MemoryStream();
        var tw = new Google.Protobuf.CodedOutputStream(tl);
        tw.WriteTag(1, Google.Protobuf.WireFormat.WireType.LengthDelimited); tw.WriteString("1001");
        foreach (var (folder, offset) in recordings)
        {
            using var rs = new MemoryStream();
            var rw = new Google.Protobuf.CodedOutputStream(rs);
            rw.WriteTag(1, Google.Protobuf.WireFormat.WireType.LengthDelimited); rw.WriteString(folder);
            rw.WriteTag(10, Google.Protobuf.WireFormat.WireType.Varint); rw.WriteInt64(offset);
            rw.Flush();
            tw.WriteTag(5, Google.Protobuf.WireFormat.WireType.LengthDelimited); tw.WriteBytes(Google.Protobuf.ByteString.CopyFrom(rs.ToArray()));
        }
        tw.Flush();
        using var outer = new MemoryStream();
        var ow = new Google.Protobuf.CodedOutputStream(outer);
        ow.WriteTag(1, Google.Protobuf.WireFormat.WireType.LengthDelimited); ow.WriteBytes(Google.Protobuf.ByteString.CopyFrom(tl.ToArray()));
        ow.Flush();
        return outer.ToArray();
    }

    [Fact]
    public void Every_recording_of_a_timeline_is_read()
    {
        var c = RecordingPb.ParseClip(ClipPbNaming(("bg_730_20250101_120000_0", 1000), ("bg_730_20250101_120000_1", 61000)));
        Assert.Empty(c.Diagnostics);
        Assert.Equal(["bg_730_20250101_120000_0", "bg_730_20250101_120000_1"], c.MediaFolders);
        Assert.Equal(61000L, c.OffsetMsForFolder("bg_730_20250101_120000_1"));
        Assert.Equal(1000L, c.OffsetMs);                                       // first recording
        var bytes = ClipPbNaming(("a", 1), ("b", 2));
        Assert.Equal(bytes, RecordingPb.ParseClip(bytes).Raw.Serialize());     // still byte-exact
    }

    [Fact]
    public async Task Clip_pb_names_the_media_folder_even_when_the_name_is_unknown()
    {
        var clip = Path.Combine(_dir, "clip_730_20250101_120500");
        await WriteMediaAsync(Path.Combine(clip, "video", "bg_730_20250101_120000"));     // a decoy with a known name
        await WriteMediaAsync(Path.Combine(clip, "video", "zz_future_layout"));
        await File.WriteAllBytesAsync(Path.Combine(clip, "clip.pb"), ClipPbNaming(("zz_future_layout", 0)));
        var r = await Scanner.ScanClipAsync(clip);
        Assert.Equal(ScanStatus.Playable, r.Status);
        Assert.EndsWith("zz_future_layout", r.Media!.Folder);
    }

    [Fact]
    public async Task Without_clip_pb_an_unknown_folder_with_init_segments_still_plays()
    {
        var clip = Path.Combine(_dir, "clip_730_20250101_120500");
        await WriteMediaAsync(Path.Combine(clip, "video", "zz_future_layout"));
        Directory.CreateDirectory(Path.Combine(clip, "video", "not_media"));
        var r = await Scanner.ScanClipAsync(clip);
        Assert.Equal(ScanStatus.Playable, r.Status);
        Assert.EndsWith("zz_future_layout", r.Media!.Folder);
    }

    [Fact]
    public void A_recording_id_cannot_escape_the_video_folder()
    {
        var clip = Path.Combine(_dir, "clip_730_20250101_120500");
        Directory.CreateDirectory(Path.Combine(clip, "video"));
        Assert.Null(Scanner.FindClipMediaFolder(clip, ".."));
        Assert.Null(Scanner.FindClipMediaFolder(clip, "../../x"));
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
