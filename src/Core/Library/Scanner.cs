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

    public static async Task<ScanResult> ScanAsync(string folder, ScanOptions? options = null, CancellationToken ct = default) =>
        (await ScanCachedAsync(folder, null, options, ct).ConfigureAwait(false)).Result;

    /// <summary>
    /// <see cref="ScanAsync"/> with a trusted set: a chunk file whose (name, length, last write) matches <paramref name="trusted"/> skips
    /// the 8-byte head read and takes the remembered result; new or changed files are read. The result also carries the head results to
    /// remember next time, the number of head reads done and the folder size. With <paramref name="trusted"/> null it behaves exactly
    /// like <see cref="ScanAsync"/>.
    /// </summary>
    public static async Task<CachedScan> ScanCachedAsync(string folder, FolderHeads? trusted, ScanOptions? options = null, CancellationToken ct = default)
    {
        options ??= new();
        var full = Path.GetFullPath(folder);
        var now = (options.Clock ?? TimeProvider.System).GetUtcNow();

        List<ScanEntry> entries;
        try { entries = List(full, ct); }
        catch (DirectoryNotFoundException) { return CachedScan.GoneResult; }
        catch (IOException) when (!Directory.Exists(full)) { return CachedScan.GoneResult; }
        catch (UnauthorizedAccessException) when (!Directory.Exists(full)) { return CachedScan.GoneResult; }

        options.AfterListing?.Invoke();

        var manifest = entries.Exists(e => e.Name == "session.mpd")
            ? await ScanManifest.ReadAsync(Path.Combine(full, "session.mpd"), ct).ConfigureAwait(false)
            : ManifestKind.Missing;

        var candidates = ScanAnalysis.HeadCandidates(entries);
        var info = new Dictionary<string, ScanEntry>(entries.Count, StringComparer.Ordinal);
        foreach (var e in entries) info[e.Name] = e;

        var heads = new HeadCheck[candidates.Count];
        var toRead = new List<int>();
        for (var i = 0; i < candidates.Count; i++)
        {
            var e = info[candidates[i]];
            if (trusted is not null && trusted.TryGet(e.Name, e.Size, e.LastWriteUtc.UtcTicks, out var known)) heads[i] = known;
            else toRead.Add(i);
        }

        var reader = options.HeadReader ?? ScanHeads.ReadAsync;
        using (var gate = new SemaphoreSlim(HeadReadParallelism))
        {
            var tasks = new Task[toRead.Count];
            for (var t = 0; t < tasks.Length; t++)
            {
                var idx = toRead[t];
                await gate.WaitAsync(ct).ConfigureAwait(false);
                tasks[t] = ReadOne(idx);
            }
            await Task.WhenAll(tasks).ConfigureAwait(false);

            async Task ReadOne(int idx)
            {
                try { heads[idx] = await reader(Path.Combine(full, candidates[idx]), ct).ConfigureAwait(false); }
                finally { gate.Release(); }
            }
        }

        var byName = new Dictionary<string, HeadCheck>(candidates.Count, StringComparer.Ordinal);
        var anyVanished = false;
        var remember = new Dictionary<string, CachedChunk>(candidates.Count, StringComparer.Ordinal);
        for (var i = 0; i < heads.Length; i++)
        {
            byName[candidates[i]] = heads[i];
            anyVanished |= heads[i] == HeadCheck.Vanished;
            if (heads[i] is HeadCheck.Valid or HeadCheck.ZeroFilled)
            {
                var e = info[candidates[i]];
                remember[candidates[i]] = new(e.Size, e.LastWriteUtc.UtcTicks, heads[i]);
            }
        }
        if (anyVanished && !Directory.Exists(full)) return CachedScan.GoneResult;

        long size = 0;
        foreach (var e in entries) size += e.Size;
        var result = ScanAnalysis.Analyze(full, entries, manifest, now, n => byName.GetValueOrDefault(n, HeadCheck.Vanished), options.Temporary);
        var tfdt = await OriginAsync(full, result, info, trusted, ct).ConfigureAwait(false);
        if (result.Media is { } m)
        {
            var shift = tfdt is { } t ? SessionMedia.AxisShiftFor(t.Seconds, t.Chunk, m.SegmentSeconds) : 0;
            result = result with { Media = m with { AxisShiftSeconds = shift, BitsPerSecond = BitsPerSecond(m, info) } };
        }
        return new CachedScan(result, FolderHeads.From(remember, tfdt), toRead.Count, size);
    }

    /// <summary>Average bitrate of the complete chunks (both streams) from the listing's sizes: no extra I/O. Null for no duration.</summary>
    internal static double? BitsPerSecond(SessionMedia media, IReadOnlyDictionary<string, ScanEntry> info)
    {
        if (media.DurationSeconds <= 0) return null;
        long bytes = 0;
        foreach (var n in media.Chunks())
            for (var s = 0; s < 2; s++)
                if (info.TryGetValue(FolderNames.ChunkFile(s, n), out var e)) bytes += e.Size;
        return bytes > 0 ? bytes * 8.0 / media.DurationSeconds : null;
    }

    /// <summary>
    /// The real tfdt of the first chunk on disk (it gives the axis shift of a saved clip): the remembered value while that file is
    /// unchanged and still the first chunk, else one read (<see cref="ScanTfdt"/>). Null when it cannot be read (the shift stays 0).
    /// </summary>
    private static async ValueTask<CachedTfdt?> OriginAsync(string folder, ScanResult result, Dictionary<string, ScanEntry> info, FolderHeads? trusted,
        CancellationToken ct)
    {
        if (result.Media?.FirstChunk is not { } first || !info.TryGetValue(FolderNames.ChunkFile(0, first), out var e)) return null;
        var ticks = e.LastWriteUtc.UtcTicks;
        if (trusted?.Tfdt is { } known && known.Matches(first, e.Size, ticks)) return known;
        return await ScanTfdt.ReadSecondsAsync(folder, first, ct).ConfigureAwait(false) is { } s ? new CachedTfdt(first, e.Size, ticks, s) : null;
    }

    /// <summary>
    /// Scans a saved clip: its chunks live in <c>clip_*/video/bg_*/</c> and are permanent (Temporary = false, whatever the inner
    /// <c>bg_*</c> name says). A vanished clip folder is <see cref="ScanStatus.Gone"/>; one without a media folder is
    /// <see cref="ScanStatus.NotPlayable"/> with <see cref="ScanIssueKind.MissingMediaFolder"/>.
    /// </summary>
    public static async Task<ScanResult> ScanClipAsync(string clipFolder, ScanOptions? options = null, CancellationToken ct = default) =>
        (await ScanClipCachedAsync(clipFolder, null, options, ct).ConfigureAwait(false)).Result;

    /// <summary><see cref="ScanClipAsync"/> with a trusted set (see <see cref="ScanCachedAsync"/>). Also returns the media folder and the parsed <c>clip.pb</c>.</summary>
    public static async Task<CachedScan> ScanClipCachedAsync(string clipFolder, FolderHeads? trusted, ScanOptions? options = null, CancellationToken ct = default)
    {
        // clip.pb names the media folder exactly (recording_id); 196 of 196 real clips matched. Without it, fall back to the name rules.
        var pb = await RecordingPb.ReadClipAsync(Path.Combine(clipFolder, "clip.pb"), ct).ConfigureAwait(false);
        var media = FindClipMediaFolder(clipFolder, pb.MediaFolders.FirstOrDefault());
        if (media is null)
        {
            var r = Directory.Exists(clipFolder)
                ? ScanResult.Gone with { Status = ScanStatus.NotPlayable, Issues = [new(ScanIssueKind.MissingMediaFolder)] }
                : ScanResult.Gone;
            return CachedScan.GoneResult with { Result = r, Clip = pb };
        }
        var scan = await ScanCachedAsync(media, trusted, (options ?? new()) with { Temporary = false }, ct).ConfigureAwait(false);
        return scan with { MediaFolder = media, Clip = pb };
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
