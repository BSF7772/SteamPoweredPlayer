// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Playback;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Tests.Fakes;
using Xunit.Abstractions;

namespace SteamPoweredPlayer.Tests.RealLibrary;

/// <summary>
/// Opt-in, read-only for the library: exports the shortest saved clip and 10 s of the newest cache session to a temp folder, lossless and
/// re-encoded, and decodes every result to the end. SPP_REAL_LIBRARY is a BackgroundRecordPath, or "auto" to read Steam's own setting.
/// Output is durations and counts only.
/// </summary>
public sealed class RealExportTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _out = Path.Combine(Path.GetTempPath(), "spp-real-export-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_out)) Directory.Delete(_out, true); } catch (IOException) { }
    }

    private static async Task<string?> RootAsync()
    {
        var r = Environment.GetEnvironmentVariable("SPP_REAL_LIBRARY");
        if (string.IsNullOrWhiteSpace(r)) return null;
        if (r != "auto") return Directory.Exists(r) ? r : null;
        var paths = SteamPaths.FromRegistry();
        if (paths is null || await paths.ReadActiveUserAsync(default) is not { } user) return null;
        return (await SteamConfig.LoadAsync(paths.LocalConfig(user.AccountId), default)).BackgroundRecordPath;
    }

    private static EncodeSettings Copy => new() { Encoder = "copy", Audio = "copy" };
    private static EncodeSettings X264 => new() { Encoder = "x264", Quality = 34, Speed = 0, Audio = "aac", AudioKbps = 64 };

    private async Task CheckAsync(string label, SessionMedia media, double start, double end, string root, bool strictVideo = false)
    {
        var plan = ExportPlan.For(media, start, end)!;
        foreach (var (name, s, expected) in new[] { ("lossless", Copy, plan.End - plan.Start), ("encoded", X264, plan.End - plan.Start) })
        {
            var sw = Stopwatch.StartNew();
            var r = await ExportService.RunAsync(new ExportJob(media, [new ExportSpan(start, end)], s, new EditSettings(), _out, label + "-" + name, root), null, default);
            var info = await Ff.ProbeAsync(r.Path);
            Assert.NotNull(info);
            Assert.True(info!.HasAudio);
            output.WriteLine($"{label} {name}: expected {expected:0.00} s, got {info.Duration:0.00} s, {r.Bytes / 1024} KiB, {sw.Elapsed.TotalSeconds:0.0} s, video {info.VideoCodec}");
            Assert.InRange(info.Duration, expected - 0.5, expected + 0.5);
            var video = await VideoSecondsAsync(r.Path);
            output.WriteLine($"  video stream {video:0.00} s");
            if (strictVideo && name == "encoded") Assert.InRange(video, expected - 0.25, expected + 0.25);
            Assert.Equal("", await DecodeErrorsAsync(r.Path));
        }
        Assert.Empty(Directory.GetDirectories(_out, ".spp-export-*", new EnumerationOptions { AttributesToSkip = 0 }));
    }

    private static async Task<string> DecodeErrorsAsync(string path)
    {
        var psi = new ProcessStartInfo(Ff.Ffmpeg) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardError = true };
        foreach (var a in new[] { "-hide_banner", "-nostdin", "-v", "error", "-i", path, "-f", "null", "-" }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var err = await p.StandardError.ReadToEndAsync();
        await p.WaitForExitAsync();
        // Steam's capture is variable-rate: decoding a lossless export to the null muxer rounds close frames onto one tick and it complains.
        // That is about the null muxer's clock, not the file (the sorted pts are strictly increasing), so only that one message is ignored.
        return string.Join("; ", err.Split((char)10, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Where(l => !l.Contains("non monotonically increasing dts to muxer", StringComparison.Ordinal)));
    }

    private static async Task<double> VideoSecondsAsync(string path)
    {
        var psi = new ProcessStartInfo(Ff.Ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-select_streams", "v:0", "-show_entries", "stream=duration", "-of", "csv=p=0", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return double.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public async Task Shortest_clip_exports_and_decodes()
    {
        if (await RootAsync() is not { } root) return;
        SessionMedia? best = null;
        var clips = Path.Combine(root, "clips");
        if (!Directory.Exists(clips)) return;
        foreach (var clip in Directory.EnumerateDirectories(clips, "clip_*"))
            if ((await Scanner.ScanClipAsync(clip)).Media is { ChunkCount: > 0 } m && (best is null || m.ChunkCount < best.ChunkCount)) best = m;
        if (best is null) return;
        var whole = ExportPlan.Whole(best)!;
        await CheckAsync("clip", best, whole.SnappedStart + 1, Math.Min(whole.SnappedEnd, whole.SnappedStart + 8), root);
    }

    private static async Task<double> AudioSecondsAsync(string path)
    {
        var psi = new ProcessStartInfo(Ff.Ffprobe) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true };
        foreach (var a in new[] { "-v", "error", "-select_streams", "a:0", "-show_entries", "stream=duration", "-of", "csv=p=0", path }) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var text = await p.StandardOutput.ReadToEndAsync();
        await p.WaitForExitAsync();
        return double.Parse(text.Trim(), System.Globalization.CultureInfo.InvariantCulture);
    }

    /// <summary>
    /// A saved clip ends where Steam meant it to (<see cref="SessionMedia.EndLimitSeconds"/>): the player's length, the export plan and the
    /// exported picture and sound all agree, instead of running up to 3 s past it. The 8 shortest clips, whole, lossless and re-encoded.
    /// </summary>
    [Fact]
    public async Task Saved_clips_play_and_export_to_their_intended_end()
    {
        if (await RootAsync() is not { } root) return;
        var clips = Path.Combine(root, "clips");
        if (!Directory.Exists(clips)) return;
        var scanned = new List<SessionMedia>();
        foreach (var clip in Directory.EnumerateDirectories(clips, "clip_*"))
            if ((await Scanner.ScanClipAsync(clip)).Media is { ChunkCount: > 0, Runs.Count: 1 } m) scanned.Add(m);
        // SPP_REAL_CLIP (optional): only the clips whose media folder name contains this text
        var only = Environment.GetEnvironmentVariable("SPP_REAL_CLIP");
        var sample = string.IsNullOrEmpty(only)
            ? scanned.OrderBy(m => m.ChunkCount).Take(8).ToList()
            : scanned.Where(m => m.Folder.Contains(only, StringComparison.Ordinal)).ToList();
        var trimmed = 0;
        foreach (var media in sample)
        {
            var first = media.StartSeconds;
            Assert.Equal(media.EndSeconds - first, media.DurationSeconds, 6);
            await using var session = new PlaybackSession(new FakeMpvPlayer());
            await session.OpenAsync(media, play: false);
            Assert.Equal(media.DurationSeconds, session.UiDuration, 6);       // the player's length, before any export
            if (media.EndLimitSeconds is not null) trimmed++;
            var whole = new ExportSpan(media.ChunkStartSeconds(media.FirstChunk!.Value), media.ChunkEndSeconds(media.LastChunk!.Value));
            Assert.Equal(media.EndSeconds, ExportPlan.Whole(media)!.End, 6);
            foreach (var (name, settings) in new[] { ("lossless", Copy), ("encoded", X264) })
            {
                var r = await ExportService.RunAsync(new ExportJob(media, [whole], settings, new EditSettings(), _out, "end-" + name, root), null, default);
                var (video, audio) = (await VideoSecondsAsync(r.Path), await AudioSecondsAsync(r.Path));
                output.WriteLine($"{media.ChunkCount} chunks, {name}: player {media.DurationSeconds:0.000} s, video {video:0.000} s, audio {audio:0.000} s, chunks {media.ChunkCount * 3.0:0.0} s");
                Assert.InRange(video, media.DurationSeconds - 0.15, media.DurationSeconds + 0.15);
                Assert.InRange(audio, media.DurationSeconds - 0.15, media.DurationSeconds + 0.15);
                File.Delete(r.Path);
            }
        }
        output.WriteLine($"{sample.Count} clips checked, {trimmed} with an intended end before the last chunk's end");
    }

    [Fact]
    public async Task Ten_seconds_of_the_newest_cache_session_export_and_decode()
    {
        if (await RootAsync() is not { } root) return;
        var video = Path.Combine(root, "video");
        if (!Directory.Exists(video)) return;
        var newest = Directory.EnumerateDirectories(video, "bg_*").OrderByDescending(Directory.GetLastWriteTimeUtc).FirstOrDefault();
        if (newest is null || (await Scanner.ScanAsync(newest)).Media is not { ChunkCount: > 4 } media) return;
        var end = media.ChunkEndSeconds(media.LastChunk!.Value);
        await CheckAsync("cache", media, end - 10.5, end - 0.5, root);        // the newest chunks: Steam's video can end before the audio here
        if (media.ChunkCount > 20) await CheckAsync("cache-mid", media, end - 40.5, end - 30.5, root, strictVideo: true);
    }
}
