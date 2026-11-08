// SPDX-License-Identifier: GPL-3.0-or-later
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Export;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Net;
using SteamPoweredPlayer.Core.Steam;
using Xunit.Abstractions;

namespace SteamPoweredPlayer.Tests.RealLibrary;

/// <summary>
/// Opt-in, read-only for the library: lists it the way the Downloads page does, downloads the shortest clip into a temp cache, then
/// scans and exports the downloaded copy. SPP_REAL_LIBRARY is a BackgroundRecordPath, or "auto". Output is counts and durations only.
/// </summary>
public sealed class RealDownloadTests(ITestOutputHelper output) : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "spp-real-dl-" + Guid.NewGuid().ToString("N")[..8]);

    public void Dispose()
    {
        try { if (Directory.Exists(_dir)) Directory.Delete(_dir, true); } catch (IOException) { }
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

    [Fact]
    public async Task Shortest_clip_downloads_scans_and_exports()
    {
        if (await RootAsync() is not { } root) return;
        var list = await RemoteLibrary.ListAsync(root, default);
        output.WriteLine($"{list.Count} recordings");
        var clip = list.Where(r => r.Name.Kind == RecordingKind.Clip).OrderBy(r => r.Media.ChunkCount).FirstOrDefault();
        if (clip is null) return;

        var cacheDir = Path.Combine(_dir, "cache");
        var cache = await LocalChunkCache.OpenAsync(cacheDir, 1L << 34);
        var queue = new DownloadQueue(cache, new SharedFolderFiles());
        var item = queue.Enqueue("net-test", clip);
        while (queue.Pending > 0) await Task.Delay(20);
        await queue.IdleAsync();
        Assert.Equal(DownloadItemState.Done, item.State);
        output.WriteLine($"{item.Progress!.Done}/{item.Progress.Total} files, {item.Progress.Bytes / 1024} KiB, {item.Progress.Missing} missing");

        var local = (await Scanner.ScanAsync(Path.Combine(cacheDir, "net-test", clip.Folder))).Media;
        Assert.NotNull(local);
        Assert.Equal(clip.Media.ChunkCount, local!.ChunkCount);
        Assert.Equal(clip.Media.AxisShiftSeconds, local.AxisShiftSeconds);

        var withMarkers = list.Where(r => r.Name.Kind == RecordingKind.Clip).OrderByDescending(r => r.Media.ChunkCount).Take(10);
        var counts = new List<int>();
        foreach (var r in withMarkers) counts.Add((await RemoteLibrary.TryLoadMarkersAsync(root, r, 60)).Count);
        output.WriteLine($"markers of the 10 longest clips: {string.Join(", ", counts)}");
        Assert.Contains(counts, c => c > 0);

        var whole = ExportPlan.Whole(local)!;
        var job = new ExportJob(local with { Live = false, Temporary = false }, [new ExportSpan(whole.SnappedStart, whole.SnappedEnd)],
            new EncodeSettings { Encoder = "copy", Audio = "copy" }, new EditSettings(), Path.Combine(_dir, "out"), "downloaded");
        var result = await ExportService.RunAsync(job, null, default);
        var info = await Ff.ProbeAsync(result.Path);
        output.WriteLine($"export {info!.Duration:0.00} s of {local.DurationSeconds:0.00} s");
        Assert.InRange(info.Duration, local.DurationSeconds - 1, local.DurationSeconds + 1);
    }
}
