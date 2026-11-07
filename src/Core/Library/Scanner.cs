// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Library;

/// <summary>
/// Thin I/O wrapper around <see cref="ScanAnalysis"/>: one directory listing, the head of <c>session.mpd</c>, and an 8-byte head read
/// of every chunk that has both streams. Everything opens with Read | Write | Delete sharing and survives files or the whole
/// folder vanishing (Steam trims the cache continuously): that gives a <see cref="ScanStatus.Gone"/> result or a dropped chunk, never an
/// exception. No polling and no watcher here (the cache follower in Phase 2 decides when to rescan).
/// </summary>
public static class Scanner
{
    /// <summary>Concurrent head reads; heads are tiny, the cost is the open.</summary>
    private const int HeadReadParallelism = 16;

    public static async Task<ScanResult> ScanAsync(string folder, ScanOptions? options = null, CancellationToken ct = default)
    {
        options ??= new();
        var full = Path.GetFullPath(folder);
        var now = (options.Clock ?? TimeProvider.System).GetUtcNow();

        List<ScanEntry> entries;
        try { entries = List(full, ct); }
        catch (DirectoryNotFoundException) { return ScanResult.Gone; }
        catch (IOException) when (!Directory.Exists(full)) { return ScanResult.Gone; }
        catch (UnauthorizedAccessException) when (!Directory.Exists(full)) { return ScanResult.Gone; }

        options.AfterListing?.Invoke();

        var manifest = entries.Exists(e => e.Name == "session.mpd")
            ? await ScanManifest.ReadAsync(Path.Combine(full, "session.mpd"), ct).ConfigureAwait(false)
            : ManifestKind.Missing;

        var candidates = ScanAnalysis.HeadCandidates(entries);
        var heads = new HeadCheck[candidates.Count];
        using (var gate = new SemaphoreSlim(HeadReadParallelism))
        {
            var tasks = new Task[candidates.Count];
            for (var i = 0; i < tasks.Length; i++)
            {
                var idx = i;
                await gate.WaitAsync(ct).ConfigureAwait(false);
                tasks[i] = ReadOne(idx);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            async Task ReadOne(int idx)
            {
                try { heads[idx] = await ScanHeads.ReadAsync(Path.Combine(full, candidates[idx]), ct).ConfigureAwait(false); }
                finally { gate.Release(); }
            }
        }

        var byName = new Dictionary<string, HeadCheck>(candidates.Count, StringComparer.Ordinal);
        var anyVanished = false;
        for (var i = 0; i < heads.Length; i++)
        {
            byName[candidates[i]] = heads[i];
            anyVanished |= heads[i] == HeadCheck.Vanished;
        }
        if (anyVanished && !Directory.Exists(full)) return ScanResult.Gone;

        return ScanAnalysis.Analyze(full, entries, manifest, now, n => byName.GetValueOrDefault(n, HeadCheck.Vanished), options.Temporary);
    }

    /// <summary>
    /// Scans a saved clip: its chunks live in <c>clip_*/video/bg_*/</c> and are permanent (Temporary = false, whatever the inner
    /// <c>bg_*</c> name says). A vanished clip folder is <see cref="ScanStatus.Gone"/>; one without a media folder is
    /// <see cref="ScanStatus.NotPlayable"/> with <see cref="ScanIssueKind.MissingMediaFolder"/>.
    /// </summary>
    public static async Task<ScanResult> ScanClipAsync(string clipFolder, ScanOptions? options = null, CancellationToken ct = default)
    {
        // clip.pb names the media folder exactly (recording_id); 196 of 196 real clips matched. Without it, fall back to the name rules.
        var pb = await RecordingPb.ReadClipAsync(Path.Combine(clipFolder, "clip.pb"), ct).ConfigureAwait(false);
        var media = FindClipMediaFolder(clipFolder, pb.MediaFolders.FirstOrDefault());
        if (media is null)
            return Directory.Exists(clipFolder)
                ? ScanResult.Gone with { Status = ScanStatus.NotPlayable, Issues = [new(ScanIssueKind.MissingMediaFolder)] }
                : ScanResult.Gone;
        return await ScanAsync(media, (options ?? new()) with { Temporary = false }, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// The folder with the chunks of a clip, <c>&lt;clip&gt;/video/&lt;media folder&gt;</c>, in this order: the folder named
    /// <paramref name="recordingId"/> (from <c>clip.pb</c>) if it exists; else a known name (<c>bg_*</c>, <c>bg_*_&lt;n&gt;</c>, <c>fg_*</c>, see
    /// <see cref="FolderNames.ParseMediaFolder"/>), the newest by name; else any subfolder holding <c>init-stream0.m4s</c> (a future
    /// layout still plays). Null when there is none or the clip folder is gone.
    /// </summary>
    public static string? FindClipMediaFolder(string clipFolder, string? recordingId = null)
    {
        try
        {
            var video = Path.Combine(Path.GetFullPath(clipFolder), "video");
            if (recordingId is { Length: > 0 } id && id.IndexOfAny(Path.GetInvalidFileNameChars()) < 0 && id is not ("." or "..")
                && Directory.Exists(Path.Combine(video, id)))
                return Path.Combine(video, id);
            string? best = null, any = null;
            foreach (var dir in Directory.EnumerateDirectories(video))
            {
                var name = Path.GetFileName(dir);
                if (FolderNames.ParseMediaFolder(name) is null)
                {
                    if (any is null && File.Exists(Path.Combine(dir, FolderNames.InitFile(0)))) any = dir;
                    continue;
                }
                if (best is null || string.CompareOrdinal(name, Path.GetFileName(best)) > 0) best = dir;
            }
            return best ?? any;
        }
        catch (DirectoryNotFoundException) { return null; }
        catch (IOException) { return null; }
        catch (UnauthorizedAccessException) { return null; }
    }

    private static List<ScanEntry> List(string folder, CancellationToken ct)
    {
        var list = new List<ScanEntry>();
        var dir = new DirectoryInfo(folder);
        foreach (var f in dir.EnumerateFiles())
        {
            ct.ThrowIfCancellationRequested();
            try { list.Add(new(f.Name, f.Length, new DateTimeOffset(f.LastWriteTimeUtc, TimeSpan.Zero))); }
            catch (FileNotFoundException) { }       // vanished while listing
            catch (DirectoryNotFoundException) { }
        }
        if (list.Count == 0 && !dir.Exists) throw new DirectoryNotFoundException(folder);
        return list;
    }
}
