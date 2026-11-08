// SPDX-License-Identifier: GPL-3.0-or-later
using FastEncoder.Core;
using SteamPoweredPlayer.Core;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Tests.Playback;

/// <summary>
/// A saved clip's tail past Steam's intended end, end to end on the synthetic clip (12 s from tfdt 585 s, the real-clip shape: the
/// manifest says Period start + duration = 585 + 1.3 + 9.1 = 595.4 s, 1.6 s before the last chunk ends). Playback with real libmpv; export
/// with ffmpeg through every path that reaches the end.
/// </summary>
public sealed class ClipEndMpvTests : IDisposable
{
    private const double Intended = ClipAxisMpvTests.Origin + 10.4;
    private readonly string _clip = ClipAxisMpvTests.GenerateClip();
    private readonly string _out = Path.Combine(Path.GetTempPath(), "spp-clip-end-out-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        foreach (var d in new[] { _clip, _out })
            try { if (Directory.Exists(d)) Directory.Delete(d, true); } catch (IOException) { }
    }

    /// <summary>The synthetic clip as the scanner reads a saved one: its own session.mpd carries the intended window.</summary>
    private async Task<SessionMedia> Scan()
    {
        // The shape of Steam's clip manifest (a media folder is a clip's own); only the numbers matter. Period start is on the tfdt axis.
        await File.WriteAllTextAsync(Path.Combine(_clip, "session.mpd"),
            "<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"static\" mediaPresentationDuration=\"PT9.1S\">\n"
            + "    <Period id=\"0\" start=\"PT9M46.3S\">\n    </Period>\n</MPD>");
        var m = (await Scanner.ScanAsync(_clip, new ScanOptions { Temporary = false })).Media!;
        return ClipEnd.Apply(m, await ScanManifest.ReadTextAsync(Path.Combine(_clip, "session.mpd"), default));
    }

    private static async Task<(double Video, double Audio)> Lengths(string path)
    {
        var info = await Ff.ProbeAsync(path);
        Assert.NotNull(info);
        Assert.True(info!.HasAudio);
        // stream durations, not the container's: the bug is one stream running past the other
        var v = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration", "-of", "csv=p=0", path], default);
        var a = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration", "-of", "csv=p=0", path], default);
        return (double.Parse(v.StdOut.Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture),
                double.Parse(a.StdOut.Trim().TrimEnd(','), System.Globalization.CultureInfo.InvariantCulture));
    }

    [Fact]
    public async Task Scanning_finds_the_intended_end()
    {
        var m = await Scan();
        Assert.Equal(ClipAxisMpvTests.Origin, m.AxisShiftSeconds);
        Assert.Equal(Intended, m.EndSeconds, 6);
        Assert.Equal(10.4, m.DurationSeconds, 6);
    }

    private static EncodeSettings Settings(string encoder) => encoder == "copy"
        ? new EncodeSettings { Encoder = "copy", Audio = "copy" }
        : new EncodeSettings { Encoder = "x264", Quality = 30, Speed = 0, Audio = "aac", AudioKbps = 64 };

    [Theory]
    [InlineData("copy")]
    [InlineData("x264")]
    public async Task A_whole_clip_export_has_picture_and_sound_ending_together_at_the_intended_end(string encoder)
    {
        var settings = Settings(encoder);
        var media = await Scan();
        var spans = ExportSpans.Plans(media, [new ExportSpan(media.ChunkStartSeconds(1), media.ChunkEndSeconds(4))], settings.Encoder == "copy");
        Assert.Equal(Intended, spans[0].End, 6);
        var r = await ExportService.RunAsync(new ExportJob(media, [new ExportSpan(media.ChunkStartSeconds(1), media.ChunkEndSeconds(4))], settings, new EditSettings(), _out, "out"), null, default);
        var (video, audio) = await Lengths(r.Path);
        Assert.InRange(video, 10.4 - 0.1, 10.4 + 0.1);               // not 12.0
        Assert.InRange(audio, 10.4 - 0.1, 10.4 + 0.1);
        Assert.InRange(Math.Abs(video - audio), 0, 0.1);
    }

    [Theory]
    [InlineData("copy")]
    [InlineData("x264")]
    public async Task A_range_that_reaches_past_the_end_stops_at_it(string encoder)
    {
        var settings = Settings(encoder);
        var media = await Scan();
        var from = ClipAxisMpvTests.Origin + 6;
        var r = await ExportService.RunAsync(new ExportJob(media, [new ExportSpan(from, from + 100)], settings, new EditSettings(), _out, "tail"), null, default);
        var (video, audio) = await Lengths(r.Path);
        Assert.InRange(video, Intended - from - 0.1, Intended - from + 0.1);
        Assert.InRange(audio, Intended - from - 0.1, Intended - from + 0.1);
    }

    [RealMpvFact]
    public async Task Playback_ends_at_the_intended_end()
    {
        var media = await Scan();
        await using var p = new MpvPlayer(new MpvPlayerOptions { Output = MpvOutput.Headless, Decode = DecodeMode.Cpu, InitialVolume = 0 });
        await using var s = new PlaybackSession(p);
        var eof = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ReachedEnd += pos => eof.TrySetResult(pos);
        Assert.Equal(10.4, media.DurationSeconds, 6);
        await s.OpenAsync(media, Intended - 1.5, play: true);
        Assert.Equal(10.4, s.UiDuration, 6);
        s.SetSpeed(4);
        var pos = await eof.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.InRange(pos, Intended - 0.15, Intended + 0.05);       // the last frame is before the end, never 1.6 s of picture past it
        Assert.True(s.AtEnd);
        // a seek into the dropped tail lands before the end as well
        await s.SeekAsync(ClipAxisMpvTests.Origin + 11.5);
        Assert.InRange(p.GetDouble("time-pos"), Intended - 0.6, Intended);
    }
}
