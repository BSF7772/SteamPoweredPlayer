// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core;
using System.Diagnostics;
using System.Text.RegularExpressions;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Tests.Playback;

/// <summary>Why the real-libmpv tests can't run here (null = they can). They need libmpv-2.dll and ffmpeg.</summary>
internal static class RealMpv
{
    private static readonly Lazy<string?> Reason = new(Compute);
    private static readonly Lazy<string> Folder = new(Generate);
    public static string? SkipReason => Reason.Value;

    private static string? Compute()
    {
        if (MpvLibrary.FindPath() is null)
        {
            // SPP_LIBMPV_DIR first (MpvLibrary reads it), else the dev tools dir of the main checkout (we may be in a worktree under it).
            for (var d = new DirectoryInfo(AppContext.BaseDirectory); d is not null; d = d.Parent)
            {
                var candidate = Path.Combine(d.FullName, ".tools", "s1", "mpv-dev");
                if (File.Exists(Path.Combine(candidate, "libmpv-2.dll"))) { MpvLibrary.SearchDirectory = candidate; break; }
            }
        }
        if (!MpvLibrary.IsAvailable()) return "libmpv-2.dll not found (set SPP_LIBMPV_DIR)";
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version") { RedirectStandardOutput = true, UseShellExecute = false, CreateNoWindow = true });
            p!.StandardOutput.ReadToEnd();
            p.WaitForExit();
            if (p.ExitCode != 0) return "ffmpeg failed";
        }
        catch (Exception e) { return "ffmpeg not available: " + e.Message; }
        return null;
    }

    /// <summary>A synthetic 30 s H.264 + AAC DASH session in Steam's file naming (3 s segments, one IDR per segment).</summary>
    public static string SessionFolder => Folder.Value;

    private static string Generate()
    {
        var dir = Path.Combine(Path.GetTempPath(), "spp-playback-tests-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(dir);
        var psi = new ProcessStartInfo("ffmpeg") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true, WorkingDirectory = dir };
        foreach (var a in new[]
        {
            "-y", "-loglevel", "error", "-f", "lavfi", "-i", "testsrc2=size=320x240:rate=30", "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", "30", "-c:v", "libx264", "-preset", "ultrafast", "-g", "90", "-keyint_min", "90", "-sc_threshold", "0", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "64k", "-f", "dash", "-seg_duration", "3", "-use_template", "1", "-use_timeline", "0",
            "-init_seg_name", "init-stream$RepresentationID$.m4s", "-media_seg_name", "chunk-stream$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", "id=0,streams=v id=1,streams=a", "session.mpd",
        }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed: " + err);
        return dir;
    }

    public static SessionMedia Media(string folder)
    {
        var re = new Regex(@"^chunk-stream0-(\d{5})\.m4s$");
        var chunks = Directory.EnumerateFiles(folder, "chunk-stream0-*.m4s")
            .Select(f => re.Match(Path.GetFileName(f))).Where(m => m.Success).Select(m => int.Parse(m.Groups[1].Value))
            .Where(n => File.Exists(Path.Combine(folder, $"chunk-stream1-{n:D5}.m4s"))).Order().ToList();
        var runs = new List<SegmentRun>();
        foreach (var n in chunks)
        {
            if (runs.Count > 0 && runs[^1].Last == n - 1) runs[^1] = runs[^1] with { Last = n };
            else runs.Add(new SegmentRun(n, n));
        }
        return new SessionMedia(folder.Replace('\\', '/'), runs, 3.0, false, false);
    }
}

/// <summary>A <see cref="FactAttribute"/> that skips (not fails) when libmpv or ffmpeg is missing.</summary>
public sealed class RealMpvFactAttribute : FactAttribute
{
    public RealMpvFactAttribute() => Skip = RealMpv.SkipReason;
}

/// <summary>Opt-in: headless libmpv (vo=null, hwdec=no) over a synthetic Steam-style session. No real recordings.</summary>
public sealed class RealMpvTests
{
    private const double Frame = 1.0 / 30;

    private static MpvPlayer NewPlayer() => new(new MpvPlayerOptions { Output = MpvOutput.Headless, Decode = DecodeMode.Cpu, InitialVolume = 0 });

    [RealMpvFact]
    public void Synthetic_session_has_ten_chunks_on_the_tfdt_axis()
    {
        var m = RealMpv.Media(RealMpv.SessionFolder);
        Assert.Equal(1, m.FirstChunk);
        Assert.InRange(m.ChunkCount, 10, 11);
    }

    [RealMpvFact]
    public async Task Cache_ceiling_is_applied()
    {
        await using var p = NewPlayer();
        Assert.Equal(MpvPlayerOptions.DefaultDemuxerMaxBytes, p.GetInt64("demuxer-max-bytes"));
        Assert.Equal(MpvPlayerOptions.DefaultDemuxerMaxBackBytes, p.GetInt64("demuxer-max-back-bytes"));
    }

    [RealMpvFact]
    public async Task Prewarm_and_open_deliver_the_first_frame()
    {
        var media = RealMpv.Media(RealMpv.SessionFolder);
        await using var p = NewPlayer();
        var (tiny, start) = PlaybackSession.BuildPreWarm(media);
        Assert.True(await p.PreWarmAsync(tiny, start));

        await using var s = new PlaybackSession(p);
        await s.OpenAsync(media, 0, play: false);
        Assert.InRange(p.GetDouble("time-pos"), 0, Frame * 1.5);
    }

    [RealMpvFact]
    public async Task Seek_by_reload_lands_within_one_frame_on_the_media_axis()
    {
        var media = RealMpv.Media(RealMpv.SessionFolder);
        await using var p = NewPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(media, 0, play: false);
        // forward, backward (a revisit: the case a plain mpv seek breaks), inside a chunk, near the end
        foreach (var t in new[] { 7.5, 19.0, 4.2, 4.5, 25.1, 1.0 })
        {
            await s.SeekAsync(t);
            var pos = p.GetDouble("time-pos");
            Assert.True(Math.Abs(pos - t) <= Frame + 0.005, $"seek {t}: time-pos {pos}");
            Assert.True(p.GetFlag("pause"));
        }
    }

    [RealMpvFact]
    public async Task Pause_is_explicit_across_reloads()
    {
        var media = RealMpv.Media(RealMpv.SessionFolder);
        await using var p = NewPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(media, 3, play: true);
        Assert.False(p.GetFlag("pause"));
        s.SetPause(true);
        await s.SeekAsync(12);
        Assert.True(p.GetFlag("pause"));
        s.SetPause(false);
        await s.SeekAsync(6);
        Assert.False(p.GetFlag("pause"));          // a reload that should play plays: pause survives loadfile otherwise
        var before = p.GetDouble("time-pos");
        await Task.Delay(600);
        Assert.True(p.GetDouble("time-pos") > before + 0.3);
    }

    [RealMpvFact]
    public async Task Speed_two_runs_at_twice_the_rate()
    {
        var media = RealMpv.Media(RealMpv.SessionFolder);
        await using var p = NewPlayer();
        await using var s = new PlaybackSession(p);
        await s.OpenAsync(media, 2, play: true);
        s.SetSpeed(2);
        await Task.Delay(300);
        var t0 = p.GetDouble("time-pos");
        var w = Stopwatch.StartNew();
        await Task.Delay(2000);
        var rate = (p.GetDouble("time-pos") - t0) / w.Elapsed.TotalSeconds;
        Assert.InRange(rate, 1.6, 2.4);
        Assert.Equal(2, p.GetDouble("speed"));
    }

    [RealMpvFact]
    public async Task A_reload_after_eof_plays_instead_of_coming_up_paused()
    {
        // found by --selftest on a growing live session: keep-open's pause at EOF must not outlive the reload that continues playback
        var media = RealMpv.Media(RealMpv.SessionFolder);
        await using var p = NewPlayer();
        await using var s = new PlaybackSession(p);
        var eof = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ReachedEnd += pos => eof.TrySetResult(pos);
        await s.OpenAsync(media, media.DurationSeconds - 1.5, play: true);
        s.SetSpeed(4);
        await eof.Task.WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(1500);                    // long enough for keep-open's pause to be in place
        await s.ReloadAsync(5, ReloadCause.EndOfWindow);
        Assert.False(p.GetFlag("pause"));
        var before = p.GetDouble("time-pos");
        await Task.Delay(600);
        Assert.True(p.GetDouble("time-pos") > before + 0.3);
    }

    [RealMpvFact]
    public async Task Eof_is_observed_with_keep_open()
    {
        var media = RealMpv.Media(RealMpv.SessionFolder);
        await using var p = NewPlayer();
        await using var s = new PlaybackSession(p);
        var eof = new TaskCompletionSource<double>(TaskCreationOptions.RunContinuationsAsynchronously);
        s.ReachedEnd += pos => eof.TrySetResult(pos);
        await s.OpenAsync(media, media.DurationSeconds - 1.5, play: true);
        s.SetSpeed(4);
        var pos = await eof.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.InRange(pos, media.DurationSeconds - 0.5, media.DurationSeconds + 0.5);
        Assert.True(p.EofReached);
        Assert.True(s.AtEnd);
    }
}
