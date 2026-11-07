// SPDX-License-Identifier: GPL-3.0-or-later
using Google.Protobuf;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Steam;

/// <summary>Tiny protobuf writer for synthetic fixtures (all values made up).</summary>
internal sealed class PbBuilder
{
    private readonly MemoryStream _ms = new();

    private PbBuilder With(Action<CodedOutputStream> write)
    {
        var o = new CodedOutputStream(_ms, leaveOpen: true);
        write(o);
        o.Flush();
        return this;
    }

    public PbBuilder Varint(int n, ulong v) => With(o => { o.WriteTag(n, WireFormat.WireType.Varint); o.WriteUInt64(v); });
    public PbBuilder Str(int n, string s) => With(o => { o.WriteTag(n, WireFormat.WireType.LengthDelimited); o.WriteString(s); });
    public PbBuilder Msg(int n, PbBuilder m) => With(o => { o.WriteTag(n, WireFormat.WireType.LengthDelimited); o.WriteBytes(ByteString.CopyFrom(m.ToArray())); });
    public PbBuilder Fixed32(int n, uint v) => With(o => { o.WriteTag(n, WireFormat.WireType.Fixed32); o.WriteFixed32(v); });
    public PbBuilder Fixed64(int n, ulong v) => With(o => { o.WriteTag(n, WireFormat.WireType.Fixed64); o.WriteFixed64(v); });
    public PbBuilder Raw(params byte[] bytes) { _ms.Write(bytes); return this; }
    public byte[] ToArray() => _ms.ToArray();
}

/// <summary>The synthetic samples. The committed copies under tests/fixtures/pb must equal these (see <see cref="RecordingPbTests"/>).</summary>
internal static class PbSamples
{
    public static byte[] GameRecording() => new PbBuilder()
        // normal session, exact offset 57332 ms (start_ms equal)
        .Msg(1, new PbBuilder().Str(1, "1001").Varint(2, 730).Varint(3, 1735732800).Varint(4, 600000)
            .Msg(5, new PbBuilder().Str(1, "bg_730_20250101_120057").Varint(2, 57332).Varint(3, 540000).Varint(10, 57332)))
        // trimmed session: field 2 differs from field 10
        .Msg(1, new PbBuilder().Str(1, "1002").Varint(2, 730).Varint(3, 1735736400).Varint(4, 300000)
            .Msg(5, new PbBuilder().Str(1, "bg_730_20250101_130010").Varint(2, 130000).Varint(3, 200000).Varint(10, 100000)))
        // no field 5 (cache folder gone)
        .Msg(1, new PbBuilder().Str(1, "1003").Varint(2, 730).Varint(3, 1735740000).Varint(4, 120000))
        // timeline id as varint instead of string, unknown fields inside
        .Msg(1, new PbBuilder().Varint(1, 1004).Varint(2, 10307921510433554432UL).Varint(3, 1735743600).Varint(4, 90000).Fixed32(9, 0xDEADBEEF)
            .Msg(5, new PbBuilder().Str(1, "bg_x_20250101_150000").Varint(10, 4000).Fixed64(11, 42)))
        .Msg(2, new PbBuilder().Str(1, "unknown field two").Varint(2, 7))
        .Msg(4, new PbBuilder().Varint(1, 730)
            .Msg(2, new PbBuilder().Str(1, "de_made_up").Str(2, "Map").Str(3, "map").Varint(4, 1))
            .Msg(3, new PbBuilder().Str(2, "1001").Varint(3, 5000))
            .Msg(3, new PbBuilder().Str(2, "1002").Varint(3, 6000)))
        .Varint(15, 1)
        .ToArray();

    public static byte[] Clip() => new PbBuilder()
        .Msg(1, new PbBuilder().Str(1, "1001").Varint(2, 730).Varint(3, 1735732800).Varint(4, 30000)
            .Msg(5, new PbBuilder().Str(1, "bg_730_20250101_120057").Varint(2, 57332).Varint(3, 30000).Varint(10, 57332)))
        .Varint(3, 1735733000)
        .Varint(4, 730)
        .Str(7, "Made up clip \U0001F3AE")
        .Varint(12, 2560)
        .Varint(13, 1440)
        .Fixed32(20, 1)
        .ToArray();
}

public sealed class RecordingPbTests
{
    private static string Fixture(string name) => Path.Combine(AppContext.BaseDirectory, "fixtures", name);

    [Fact]
    public void GameRecording_reads_timelines_with_exact_offsets()
    {
        var r = RecordingPb.ParseGameRecording(PbSamples.GameRecording());
        Assert.Empty(r.Diagnostics);
        Assert.Equal(4, r.Timelines.Count);
        var t = r.Timelines[0];
        Assert.Equal("1001", t.TimelineId);
        Assert.Equal(730UL, t.GameId);
        Assert.Equal(1735732800L, t.DateRecordedUnix);
        Assert.Equal(600000L, t.DurationMs);
        Assert.Equal("bg_730_20250101_120057", t.Session!.BgFolder);
        Assert.Equal(57332L, r.OffsetMsFor("1001"));
        Assert.Equal(57332L, r.OffsetMsForFolder("bg_730_20250101_120057"));
    }

    [Fact]
    public void Trimmed_session_uses_field_10_not_field_2()
    {
        var r = RecordingPb.ParseGameRecording(PbSamples.GameRecording());
        var s = r.Timelines[1].Session!;
        Assert.Equal(130000L, s.StartMs);
        Assert.Equal(200000L, s.DurationMs);
        Assert.Equal(100000L, s.OffsetMs);
        Assert.Equal(100000L, r.OffsetMsFor("1002"));
    }

    [Fact]
    public void Missing_field_5_gives_no_offset_without_diagnostics()
    {
        var r = RecordingPb.ParseGameRecording(PbSamples.GameRecording());
        Assert.Null(r.Timelines[2].Session);
        Assert.Null(r.OffsetMsFor("1003"));
        Assert.Null(r.OffsetMsFor("does-not-exist"));
        Assert.Empty(r.Diagnostics);
    }

    [Fact]
    public void Varint_timeline_id_becomes_text_and_shortcut_game_id_is_kept()
    {
        var t = RecordingPb.ParseGameRecording(PbSamples.GameRecording()).Timelines[3];
        Assert.Equal("1004", t.TimelineId);
        Assert.Equal(10307921510433554432UL, t.GameId);
        Assert.Equal(4000L, t.Session!.OffsetMs);
    }

    [Fact]
    public void Tag_index_is_read()
    {
        var e = Assert.Single(RecordingPb.ParseGameRecording(PbSamples.GameRecording()).TagIndex);
        Assert.Equal(730UL, e.AppId);
        Assert.Equal(new PbTag("de_made_up", "Map", "map", 1), e.Tag);
        Assert.Equal([new PbTagOccurrence("1001", 5000), new PbTagOccurrence("1002", 6000)], e.Occurrences);
    }

    [Fact]
    public void GameRecording_round_trips_byte_identically_including_unknown_fields()
    {
        var bytes = PbSamples.GameRecording();
        var r = RecordingPb.ParseGameRecording(bytes);
        Assert.Equal(bytes, r.Raw.Serialize());
        Assert.NotNull(r.Raw.First(2));   // unknown field 2 kept
        Assert.NotNull(r.Raw.First(15));  // unknown field 15 kept
        Assert.NotNull(r.Timelines[3].Raw.First(9)); // unknown inside an entry
        Assert.NotNull(r.Timelines[3].Session!.Raw.First(11));
    }

    [Fact]
    public void Non_canonical_varints_still_round_trip()
    {
        byte[] bytes = [0x08, 0x81, 0x80, 0x00, 0x10, 0x05];   // field 1 = 1 with padding bytes, field 2 = 5
        var m = PbMessage.Parse(bytes);
        Assert.Null(m.Error);
        Assert.Equal(1UL, m.First(1)!.VarintValue);
        Assert.Equal(bytes, m.Serialize());
    }

    [Fact]
    public void Clip_reads_session_offset_title_and_size()
    {
        var c = RecordingPb.ParseClip(PbSamples.Clip());
        Assert.Empty(c.Diagnostics);
        Assert.Equal("1001", c.Timeline!.TimelineId);
        Assert.Equal(57332L, c.OffsetMs);
        Assert.Equal("bg_730_20250101_120057", c.Timeline.Session!.BgFolder);
        Assert.Equal(30000L, c.Timeline.Session.DurationMs);
        Assert.Equal(1735733000L, c.CreatedUnix);
        Assert.Equal(730UL, c.GameId);
        Assert.Equal("Made up clip \U0001F3AE", c.Title);
        Assert.Equal(2560, c.Width);
        Assert.Equal(1440, c.Height);
        Assert.Equal(PbSamples.Clip(), c.Raw.Serialize());
        Assert.NotNull(c.Raw.First(20));
    }

    [Fact]
    public void Clip_with_surprising_wire_types_reports_diagnostics_and_keeps_bytes()
    {
        var bytes = new PbBuilder().Varint(1, 5)                 // timeline should be a message
            .Str(3, "1735733000")                                 // created as decimal text: accepted
            .Varint(7, 99)                                        // title as varint: becomes "99"
            .Fixed64(12, 1920).ToArray();                         // width as fixed64: accepted
        var c = RecordingPb.ParseClip(bytes);
        Assert.Null(c.Timeline);
        Assert.Single(c.Diagnostics);
        Assert.Equal(1735733000L, c.CreatedUnix);
        Assert.Equal("99", c.Title);
        Assert.Equal(1920, c.Width);
        Assert.Equal(bytes, c.Raw.Serialize());
    }

    [Fact]
    public void Empty_input_is_an_empty_result()
    {
        var r = RecordingPb.ParseGameRecording(ReadOnlyMemory<byte>.Empty);
        Assert.Empty(r.Timelines);
        Assert.Empty(r.Diagnostics);
        Assert.Empty(r.Raw.Serialize());
        Assert.Empty(RecordingPb.ParseClip(ReadOnlyMemory<byte>.Empty).Diagnostics);
    }

    [Fact]
    public void Truncated_input_keeps_what_parsed_and_the_rest_as_trailing()
    {
        var bytes = PbSamples.GameRecording();
        var cut = bytes[..(bytes.Length - 3)];
        var r = RecordingPb.ParseGameRecording(cut);
        Assert.NotEmpty(r.Diagnostics);
        Assert.NotEmpty(r.Timelines);
        Assert.Equal(cut, r.Raw.Serialize());
    }

    [Fact]
    public void Garbage_never_throws_and_always_round_trips()
    {
        var rng = new Random(12345);
        var valid = PbSamples.GameRecording();
        for (int i = 0; i < 2000; i++)
        {
            byte[] data;
            if (i % 3 == 0) { data = new byte[rng.Next(0, 80)]; rng.NextBytes(data); }
            else
            {
                data = (byte[])valid.Clone();
                for (int k = 0; k < rng.Next(1, 6); k++) data[rng.Next(data.Length)] = (byte)rng.Next(256);
                if (i % 2 == 0) data = data[..rng.Next(data.Length)];
            }
            var g = RecordingPb.ParseGameRecording(data);
            var c = RecordingPb.ParseClip(data);
            Assert.Equal(data, g.Raw.Serialize());
            Assert.Equal(data, c.Raw.Serialize());
        }
    }

    [Fact]
    public void Overlong_varint_and_huge_length_are_errors_not_exceptions()
    {
        var m = PbMessage.Parse(new byte[] { 0x08, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x01 });
        Assert.NotNull(m.Error);
        var m2 = PbMessage.Parse(new byte[] { 0x0A, 0xFF, 0xFF, 0xFF, 0xFF, 0x0F, 1, 2 });
        Assert.NotNull(m2.Error);
        Assert.NotNull(PbMessage.Parse(new byte[] { 0x0B, 0x01 }).Error);   // group wire type
        Assert.NotNull(PbMessage.Parse(new byte[] { 0x00, 0x01 }).Error);   // field number 0
    }

    [Fact]
    public async Task Files_are_read_with_full_sharing_and_missing_files_report_a_diagnostic()
    {
        var dir = Directory.CreateTempSubdirectory("spp-pb-").FullName;
        try
        {
            var path = Path.Combine(dir, "gamerecording.pb");
            await File.WriteAllBytesAsync(path, PbSamples.GameRecording());
            await using (new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete))   // "Steam" holds it open
            {
                var r = await RecordingPb.ReadGameRecordingAsync(path);
                Assert.Equal(4, r.Timelines.Count);
            }
            var missing = await RecordingPb.ReadClipAsync(Path.Combine(dir, "nope.pb"));
            Assert.Single(missing.Diagnostics);
            Assert.Null(missing.Timeline);
            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => RecordingPb.ReadGameRecordingAsync(path, new CancellationToken(canceled: true)));
        }
        finally { Directory.Delete(dir, true); }
    }

    [Theory]
    [InlineData("pb/gamerecording-basic.pb", true)]
    [InlineData("pb/clip-basic.pb", false)]
    public void Committed_fixture_equals_the_builder_output(string name, bool gameRecording)
    {
        var expected = gameRecording ? PbSamples.GameRecording() : PbSamples.Clip();
        var path = Fixture(name);
        Assert.True(File.Exists(path), $"missing fixture {name}");
        Assert.Equal(expected, File.ReadAllBytes(path));
    }

    /// <summary>Opt-in (SPP_REAL_LIBRARY = a BackgroundRecordPath): every real clip.pb and gamerecording.pb parses with 0 diagnostics and round-trips. Read-only.</summary>
    [Fact]
    public async Task Real_library_pb_files_parse_without_errors()
    {
        var root = Environment.GetEnvironmentVariable("SPP_REAL_LIBRARY");
        if (string.IsNullOrWhiteSpace(root) || !Directory.Exists(root)) return;   // opt-in
        int n = 0;
        foreach (var file in Directory.EnumerateFiles(root, "*.pb", SearchOption.AllDirectories))
        {
            var name = Path.GetFileName(file);
            if (name is not ("clip.pb" or "gamerecording.pb")) continue;
            // read + write + delete sharing so Steam's cleanup never fails; null = Steam trimmed it while we scanned
            if (await RealLibrary.RealLibraryTests.ReadSharedAsync(file) is not { } bytes) continue;
            if (name == "clip.pb")
            {
                var c = RecordingPb.ParseClip(bytes);
                Assert.True(c.Diagnostics.Count == 0, $"{file}: {string.Join("; ", c.Diagnostics)}");
                Assert.Equal(bytes, c.Raw.Serialize());
            }
            else
            {
                var g = RecordingPb.ParseGameRecording(bytes);
                Assert.True(g.Diagnostics.Count == 0, $"{file}: {string.Join("; ", g.Diagnostics)}");
                Assert.Equal(bytes, g.Raw.Serialize());
            }
            n++;
        }
        Assert.True(n > 0, "SPP_REAL_LIBRARY is set but contains no clip.pb / gamerecording.pb");
    }
}
