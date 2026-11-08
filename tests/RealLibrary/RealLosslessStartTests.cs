// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;
using Xunit.Abstractions;

namespace SteamPoweredPlayer.Tests.RealLibrary;

/// <summary>
/// Opt-in, read-only for the library (SPP_REAL_LIBRARY): Save clip on real chunks, mid-chunk A, sync 0 and ±1.8 s. The file must play from
/// A (edit list over the keyframe's lead-in), both streams must start together and end on B. Output is numbers only. When SPP_REAL_KEEP
/// names a folder the exports are copied there for a manual look with other players.
/// </summary>
public sealed class RealLosslessStartTests(ITestOutputHelper output) : IDisposable
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;
    private readonly string _out = Path.Combine(Path.GetTempPath(), "spp-real-lossless-" + Guid.NewGuid().ToString("N")[..8]);

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

    private static async Task<List<(double Pts, string Md5)>> FramesAsync(string file, bool copyts, bool ignoreEditList, int? count)
    {
        var args = new List<string> { "-v", "error" };
        if (copyts) args.Add("-copyts");
        if (ignoreEditList) args.AddRange(["-ignore_editlist", "1"]);
        args.AddRange(["-i", file, "-map", "0:v:0", "-fps_mode", "passthrough"]);
        if (count is { } n) args.AddRange(["-frames:v", n.ToString(Inv)]);
        args.AddRange(["-f", "framemd5", "-"]);
        var r = await Ff.RunAsync(Ff.Ffmpeg, args);
        var tb = double.Parse(r.StdOut.Split('\n').First(l => l.StartsWith("#tb", StringComparison.Ordinal)).Split("1/")[1], Inv);
        return [.. r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Where(l => l[0] != '#')
            .Select(l => l.Split(',', StringSplitOptions.TrimEntries)).Select(p => (double.Parse(p[2], Inv) / tb, p[5]))];
    }

    private static async Task<(double VStart, double VEnd, double AStart, double AEnd)> StreamsAsync(string path)
    {
        var r = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_type,start_time,duration", "-of", "csv=p=0", path]);
        var s = r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Select(l => l.Split(','))
            .Where(p => p[0] is "video" or "audio").ToDictionary(p => p[0], p => (Start: double.Parse(p[1], Inv), End: double.Parse(p[1], Inv) + double.Parse(p[2], Inv)));
        return (s["video"].Start, s["video"].End, s["audio"].Start, s["audio"].End);
    }

    [Fact]
    public async Task Save_clip_on_real_chunks_plays_from_a()
    {
        if (await RootAsync() is not { } root) return;
        var video = Path.Combine(root, "video");
        if (!Directory.Exists(video)) return;
        // A cache session with a long run of chunks: not the active one (the newest), so nothing moves under the test.
        var media = (await Task.WhenAll(Directory.EnumerateDirectories(video, "bg_*").OrderByDescending(Directory.GetLastWriteTimeUtc).Skip(1).Take(8)
            .Select(async d => (await Scanner.ScanAsync(d)).Media))).FirstOrDefault(m => m is { ChunkCount: > 12 });
        if (media is null) return;
        if (!Ff.Available) Ff.Locate();
        var chunk = media.Chunks().Skip(6).First();
        var keyframe = media.ChunkStartSeconds(chunk);
        const double lead = 1.7, length = 5.3;
        double a = keyframe + lead, b = a + length;

        var joined = Path.Combine(_out, "source.mp4");
        Directory.CreateDirectory(_out);
        await using (var target = File.Create(joined))
            foreach (var name in new[] { "init-stream0.m4s" }.Concat(media.Chunks().Where(c => c >= chunk - 1 && c <= chunk + 3).Select(c => FolderNames.ChunkFile(0, c))))
                await target.WriteAsync(await File.ReadAllBytesAsync(Path.Combine(media.Folder, name)));
        var source = await FramesAsync(joined, copyts: true, ignoreEditList: false, count: null);
        var keep = Environment.GetEnvironmentVariable("SPP_REAL_KEEP");

        foreach (var delay in new[] { 0, -1.8, 1.8 })
        {
            var job = new ExportJob(media, [new ExportSpan(a, b)], new EncodeSettings { Encoder = "copy", Audio = "copy" }, new EditSettings(), _out, $"clip{delay.ToString(Inv)}", root,
                delay, [new ExportChapter(a + 2, "marker at A+2"), new ExportChapter(keyframe + 0.5, "before A")]);
            var r = await ExportService.RunAsync(job, null, default);
            var info = await Ff.ProbeAsync(r.Path);
            var s = await StreamsAsync(r.Path);
            var firstShown = (await FramesAsync(r.Path, false, false, 1))[0].Md5;
            var firstIgnoring = (await FramesAsync(r.Path, false, true, 1))[0].Md5;
            var shown = source.Where(f => f.Md5 == firstShown).Select(f => f.Pts).ToList();
            var ignored = source.Where(f => f.Md5 == firstIgnoring).Select(f => f.Pts).ToList();
            var chapters = (await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-show_entries", "chapter=start_time", "-of", "csv=p=0", r.Path])).StdOut.Replace('\n', ' ').Trim();
            output.WriteLine($"delay {delay:0.0}: B-A {length:0.000}, format {info!.Duration:0.000}; video {s.VStart:0.000}..{s.VEnd:0.000}, audio {s.AStart:0.000}..{s.AEnd:0.000}; "
                + $"first shown frame at A{(shown.Count == 1 ? shown[0] - a : double.NaN):+0.000;-0.000} s, ignoring the edit list at keyframe{(ignored.Count == 1 ? ignored[0] - keyframe : double.NaN):+0.000;-0.000} s; "
                + $"chapters {chapters}; {r.Bytes / 1024} KiB");
            if (keep is { Length: > 0 } && Directory.Exists(keep)) File.Copy(r.Path, Path.Combine(keep, Path.GetFileName(r.Path)), true);

            Assert.InRange(info.Duration, length - 0.05, length + 0.1);
            Assert.Single(shown);
            Assert.InRange(shown[0] - a, -0.002, 0.034);
            Assert.InRange(s.VStart, 0, 0.05);
            Assert.InRange(s.AStart, 0, 0.05);
            Assert.InRange(s.AEnd, length - 0.05, length + 0.05);
        }
    }
}
