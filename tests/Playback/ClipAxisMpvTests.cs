// SPDX-License-Identifier: GPL-3.0-or-later
using System.Buffers.Binary;
using System.Diagnostics;
using SteamPoweredPlayer.Core;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Tests.Playback;

/// <summary>
/// Phase 2.1 §1.1 with real libmpv: a synthetic clip-like session (chunks numbered from 1, tfdt starting at 585 s, like a saved clip) and,
/// opt-in, the shortest real clip of SPP_REAL_LIBRARY (read-only; nothing about it is written anywhere).
/// </summary>
public sealed class ClipAxisMpvTests
{
    private const double Origin = 585;
    private static readonly Lazy<string> ClipFolder = new(GenerateClip);

    private static MpvPlayer NewPlayer() => new(new MpvPlayerOptions { Output = MpvOutput.Headless, Decode = DecodeMode.Cpu, InitialVolume = 0 });

    /// <summary>12 s H.264 30 fps + AAC, Steam naming, every timestamp shifted by <see cref="Origin"/> (tfdt of chunk 1 = 585 s).</summary>
    private static string GenerateClip()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spp-clip-axis-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in new[]
        {
            "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30:duration=12",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000:duration=12", "-c:v", "libx264", "-preset", "ultrafast", "-g", "90", "-keyint_min", "90", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "64k", "-f", "dash", "-seg_duration", "3", "-use_template", "1", "-use_timeline", "0",
            "-init_seg_name", "init-stream$RepresentationID$.m4s", "-media_seg_name", "chunk-stream$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", "id=0,streams=v id=1,streams=a", "session.mpd",
        }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed: " + err);
        // ffmpeg's dash muxer starts tfdt at 0 whatever the input timestamps (-output_ts_offset only moves the sidx / edit list), while
        // Steam's clips carry the session's absolute tfdt: add the origin to every tfdt box, in each track's own timescale.
        for (var stream = 0; stream < 2; stream++)
        {
            var ts = FirstFragment.Timescale(File.ReadAllBytes(Path.Combine(dir, $"init-stream{stream}.m4s")))
                ?? throw new InvalidOperationException("no timescale");
            foreach (var chunk in Directory.EnumerateFiles(dir, $"chunk-stream{stream}-*.m4s"))
            {
                var b = File.ReadAllBytes(chunk);
                ShiftTfdt(b, (ulong)(Origin * ts));
                File.WriteAllBytes(chunk, b);
            }
        }
        return dir;
    }

    /// <summary>Adds <paramref name="delta"/> to every <c>tfdt</c> box (version 0 or 1) found in <paramref name="b"/>.</summary>
    private static void ShiftTfdt(byte[] b, ulong delta)
    {
        var span = b.AsSpan();
        for (var i = 4; i + 16 <= b.Length; i++)
        {
            if (!span.Slice(i, 4).SequenceEqual("tfdt"u8)) continue;
            var body = span[(i + 4)..];
            if (body[0] == 1) BinaryPrimitives.WriteUInt64BigEndian(body[4..], BinaryPrimitives.ReadUInt64BigEndian(body[4..]) + delta);
            else BinaryPrimitives.WriteUInt32BigEndian(body[4..], checked((uint)(BinaryPrimitives.ReadUInt32BigEndian(body[4..]) + delta)));
        }
    }

    private static async Task<SessionMedia> ScanClip(string folder) =>
        (await Scanner.ScanAsync(folder, new ScanOptions { Temporary = false })).Media ?? throw new InvalidOperationException("not playable");

    [RealMpvFact]
    public async Task Synthetic_clip_scans_to_its_tfdt_origin_and_plays_from_ui_zero()
    {
        var media = await ScanClip(ClipFolder.Value);
        Assert.Equal(1, media.FirstChunk);
        Assert.Equal(Origin, media.AxisShiftSeconds);

        await using var p = NewPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(media, play: false);
        Assert.InRange(p.GetDouble("time-pos"), Origin, Origin + 0.05);
        Assert.InRange(s.UiPosition, 0, 0.05);
        Assert.Equal(media.ChunkCount * 3.0, s.UiDuration);

        await s.SeekUiAsync(s.UiPosition + 5);                     // the +5 s key from 0
        Assert.InRange(p.GetDouble("time-pos") - Origin, 5 - 1 / 30.0 - 0.005, 5 + 1 / 30.0 + 0.005);
        Assert.Equal(DemuxerCache.For(media).MaxBytes, p.GetInt64("demuxer-max-bytes"));
        Assert.Equal(DemuxerCache.For(media).MaxBackBytes, p.GetInt64("demuxer-max-back-bytes"));
    }

    [RealMpvFact]
    public async Task Synthetic_clip_audio_starts_with_the_first_frame_after_open_and_after_a_reload()
    {
        var media = await ScanClip(ClipFolder.Value);
        await using var p = NewPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(media, play: true);
        await AssertAudioFollows(p, Origin);
        await s.SeekAsync(Origin + 6.5);                           // reload into chunk 3
        await AssertAudioFollows(p, Origin + 6.5);
    }

    /// <summary>Within 0.5 s of play, mpv has an audio pts that is within 0.25 s of the video (no silent catch-up phase).</summary>
    private static async Task AssertAudioFollows(MpvPlayer p, double from)
    {
        var deadline = Stopwatch.StartNew();
        double audio = double.NaN, video = double.NaN;
        while (deadline.Elapsed < TimeSpan.FromSeconds(0.5))
        {
            audio = p.GetDouble("audio-pts");
            video = p.GetDouble("time-pos");
            if (!double.IsNaN(audio) && Math.Abs(audio - video) < 0.25 && video > from + 0.05) return;
            await Task.Delay(20);
        }
        Assert.Fail($"audio does not follow the video: audio-pts {audio}, time-pos {video} (started at {from})");
    }

    // ---- opt-in: the shortest real clip ----------------------------------------------------------------------------

    private static string? ShortestRealClip()
    {
        var lib = Environment.GetEnvironmentVariable("SPP_REAL_LIBRARY");
        if (string.IsNullOrEmpty(lib) || !Directory.Exists(Path.Combine(lib, "clips"))) return null;
        string? best = null;
        var bestCount = int.MaxValue;
        foreach (var c in Directory.EnumerateDirectories(Path.Combine(lib, "clips")))
        {
            if (Scanner.FindClipMediaFolder(c) is not { } m) continue;
            var n = Directory.EnumerateFiles(m, "chunk-stream0-*.m4s").Count();
            if (n > 0 && n < bestCount) { bestCount = n; best = c; }
        }
        return best;
    }

    [RealMpvFact]
    public async Task Real_shortest_clip_ui_axis_runs_from_zero_to_its_duration()
    {
        if (ShortestRealClip() is not { } clip) return;            // opt-in (SPP_REAL_LIBRARY unset): nothing to check
        var media = (await Scanner.ScanClipAsync(clip)).Media!;
        Assert.True(media.AxisShiftSeconds > 0, "a saved clip keeps the session's absolute tfdt");

        await using var p = NewPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(media, play: false);
        var fps = p.GetDouble("container-fps") is var f && f > 0 ? f : 60;
        var frame = 1 / fps;
        Assert.InRange(s.UiPosition, 0, 0.1);                      // real chunks start up to ~0.07 s after the grid
        Assert.Equal(media.ChunkCount * 3.0, s.UiDuration);

        await s.SeekUiAsync(5);
        Assert.InRange(s.MediaToUi(p.GetDouble("time-pos")), 5 - frame - 0.005, 5 + frame + 0.005);

        await s.SeekUiAsync(s.UiDuration + 10);                    // past the end: the last frame, not the start
        var end = s.MediaToUi(p.GetDouble("time-pos"));
        Assert.InRange(end, s.UiDuration - 0.5, s.UiDuration);
    }
}
