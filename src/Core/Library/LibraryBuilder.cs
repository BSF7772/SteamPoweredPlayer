// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Library;

/// <summary>A recording folder under the recording root: <c>video/bg_*</c> or <c>clips/clip_*</c>.</summary>
/// <param name="RelativeFolder">Relative to the recording root, forward slashes: the key used by <c>library.json</c> and the scan cache.</param>
public sealed record RecordingFolder(RecordingKind Kind, string RelativeFolder, string FullPath, RecordingName Name);

/// <summary>Per-pass Steam data shared by all folders of a pass: <c>gamerecording.pb</c> and the timeline files. Build with <see cref="LibraryBuilder.LoadContextAsync"/>.</summary>
public sealed class RecordingContext
{
    public static RecordingContext Empty { get; } = new(null, [], new Dictionary<string, string>(0));

    internal RecordingContext(GameRecordingPb? pb, IReadOnlyList<(string Path, TimelineName Name)> timelines, Dictionary<string, string> byId)
    {
        Pb = pb;
        Timelines = timelines;
        ById = byId;
    }

    internal GameRecordingPb? Pb { get; }
    internal IReadOnlyList<(string Path, TimelineName Name)> Timelines { get; }

    /// <summary>Timeline file path by timeline id (the file name without <c>timeline_</c> and <c>.json</c>).</summary>
    internal Dictionary<string, string> ById { get; }
}

/// <summary>Outcome of scanning one recording folder. <see cref="Entry"/> is set only when the folder is playable.</summary>
/// <param name="Failed">An unexpected error: the caller keeps whatever it knew about the folder (no change, no removal).</param>
public sealed record FolderScan(RecordingFolder Folder, ScanResult Result, FolderHeads Heads, int HeadReads, RecordingEntry? Entry, bool Failed = false);

/// <summary>Everything one full pass produced.</summary>
public sealed record LibraryScan(LibraryIndex Index, ScanCache Cache, int Folders, int HeadReads, TimeSpan Elapsed);

/// <summary>
/// Builds <see cref="LibraryIndex"/> entries from the recording root: enumerates <c>video/bg_*</c> and <c>clips/clip_*</c>, scans each
/// with <see cref="Scanner"/> (using remembered chunk heads), adds markers from timelines and <c>gamerecording.pb</c> / <c>clip.pb</c>,
/// resolves game names and copies art. Reads only; the one write is the art copy into the data folder, through <see cref="ArtworkStore"/>.
/// Not thread-safe except <see cref="ScanFolderAsync"/> and <see cref="ScanFoldersAsync"/>, which may run concurrently with each other;
/// <see cref="AssembleAsync"/> and <see cref="LoadContextAsync"/> must be called one at a time.
/// </summary>
public sealed class LibraryBuilder
{
    private readonly ScanOptions _scan;
    private readonly TimeProvider _clock;
    private readonly GameNameResolver? _names;
    private readonly Dictionary<ulong, GameEntry> _games = [];     // resolved this session or seeded from the previous index
    private readonly HashSet<ulong> _artDone = [];
    private (long Length, long Ticks)? _pbStamp;
    private GameRecordingPb? _pb;

    /// <summary>Where art is copied to. Null (data folder not confirmed) means no art is copied and games keep the art of the seeded index.</summary>
    public ArtworkStore? Artwork { get; set; }

    /// <param name="recordingRoot">Steam's <c>BackgroundRecordPath</c>.</param>
    /// <param name="names">Game names and art sources; null when Steam is not installed (names come from the seeded index, else "App N").</param>
    public LibraryBuilder(string recordingRoot, GameNameResolver? names = null, ArtworkStore? artwork = null, TimeProvider? clock = null, ScanOptions? scan = null)
    {
        Root = Path.GetFullPath(recordingRoot);
        _names = names;
        Artwork = artwork;
        _clock = clock ?? TimeProvider.System;
        _scan = (scan ?? new()) with { Clock = _clock };
    }

    public string Root { get; }

    /// <summary>Takes the names and art of a previous index (a name is kept unless it is the "App N" fallback, which is looked up again).</summary>
    public void Seed(LibraryIndex? previous)
    {
        if (previous is null) return;
        foreach (var g in previous.Games)
            if (ulong.TryParse(g.GameId, NumberStyles.None, CultureInfo.InvariantCulture, out var id) && g.Name != GameNameResolver.Fallback(id))
                _games.TryAdd(id, g);
    }

    /// <summary>Forget which games had their art copied (call after the data folder was confirmed or changed).</summary>
    public void ResetArt() => _artDone.Clear();

    // ------------------------------------------------------------------------------------------------- enumeration

    /// <summary>The recording folders on disk now, newest first. A missing <c>video</c> or <c>clips</c> folder is simply empty.</summary>
    public IReadOnlyList<RecordingFolder> Enumerate()
    {
        var list = new List<RecordingFolder>();
        foreach (var (sub, prefix) in new[] { ("video", "bg_"), ("clips", "clip_") })
        {
            try
            {
                foreach (var dir in Directory.EnumerateDirectories(Path.Combine(Root, sub), prefix + "*"))
                    if (FolderFor(sub + "/" + Path.GetFileName(dir)) is { } f) list.Add(f);
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* folder missing or vanishing: nothing there */ }
        }
        list.Sort(static (a, b) =>
        {
            var c = b.Name.StartUtc.CompareTo(a.Name.StartUtc);
            return c != 0 ? c : string.CompareOrdinal(a.RelativeFolder, b.RelativeFolder);
        });
        return list;
    }

    /// <summary>The folder for <paramref name="relativeFolder"/> (<c>video/bg_*</c> or <c>clips/clip_*</c>); null when the name is neither.</summary>
    public RecordingFolder? FolderFor(string relativeFolder)
    {
        var parts = relativeFolder.Split('/');
        if (parts.Length != 2 || FolderNames.ParseRecording(parts[1]) is not { } name) return null;
        var ok = parts[0] switch
        {
            "video" => name.Kind == RecordingKind.CacheSession,
            "clips" => name.Kind is RecordingKind.Clip or RecordingKind.DownloadedClip,
            _ => false,
        };
        return ok ? new(name.Kind, relativeFolder, Path.Combine(Root, parts[0], parts[1]), name) : null;
    }

    // ---------------------------------------------------------------------------------------------------- context

    /// <summary><c>gamerecording.pb</c> (re-parsed only when its size or time changed) and the timeline file list.</summary>
    public async Task<RecordingContext> LoadContextAsync(CancellationToken ct = default)
    {
        var pbPath = Path.Combine(Root, "gamerecording.pb");
        try
        {
            var fi = new FileInfo(pbPath);
            if (!fi.Exists) { _pb = null; _pbStamp = null; }
            else if (_pbStamp != (fi.Length, fi.LastWriteTimeUtc.Ticks) || _pb is null)
            {
                _pb = await RecordingPb.ReadGameRecordingAsync(pbPath, ct).ConfigureAwait(false);
                _pbStamp = (fi.Length, fi.LastWriteTimeUtc.Ticks);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { _pb = null; _pbStamp = null; }

        var list = new List<(string, TimelineName)>();
        var byId = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            foreach (var path in Directory.EnumerateFiles(Path.Combine(Root, "timelines"), "timeline_*.json"))
            {
                var file = Path.GetFileName(path);
                if (FolderNames.ParseTimeline(file) is not { } tn) continue;
                list.Add((path, tn));
                byId[file["timeline_".Length..^".json".Length]] = path;
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* none */ }
        return new(_pb, list, byId);
    }

    // ------------------------------------------------------------------------------------------------ one folder

    /// <summary>
    /// Scans one folder with its remembered heads. Never throws except for cancellation: an unexpected error is a
    /// <see cref="FolderScan.Failed"/> result so one bad folder cannot stop a pass.
    /// </summary>
    public async Task<FolderScan> ScanFolderAsync(RecordingFolder folder, FolderHeads? trusted, RecordingContext context, CancellationToken ct = default)
    {
        try
        {
            var isClip = folder.Kind != RecordingKind.CacheSession;
            var scan = isClip
                ? await Scanner.ScanClipCachedAsync(folder.FullPath, trusted, _scan, ct).ConfigureAwait(false)
                : await Scanner.ScanCachedAsync(folder.FullPath, trusted, _scan, ct).ConfigureAwait(false);
            if (scan.Result.Media is not { } media) return new(folder, scan.Result, scan.Heads, scan.HeadReads, null);

            var entry = isClip
                ? await ClipEntryAsync(folder, scan, media, ct).ConfigureAwait(false)
                : await CacheEntryAsync(folder, scan, media, context, ct).ConfigureAwait(false);
            return new(folder, scan.Result, scan.Heads, scan.HeadReads, entry);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or ArgumentException)
        {
            return new(folder, ScanResult.Gone, trusted ?? FolderHeads.Empty, 0, null, Failed: true);
        }
    }

    private async Task<RecordingEntry> CacheEntryAsync(RecordingFolder f, CachedScan scan, SessionMedia media, RecordingContext ctx, CancellationToken ct)
    {
        var name = Path.GetFileName(f.FullPath);
        var offset = ctx.Pb?.OffsetMsForFolder(name);
        string? timeline = null;
        var tid = ctx.Pb?.Timelines.FirstOrDefault(t => t.Recordings.Any(r => string.Equals(r.BgFolder, name, StringComparison.Ordinal)))?.TimelineId;
        if (tid is not null) ctx.ById.TryGetValue(tid, out timeline);
        if (timeline is null && f.Name.GameId is { } gid)
        {
            // The timeline of a session: same game, latest start at or before the session start (a timeline can start ~113 s earlier).
            DateTimeOffset best = default;
            foreach (var (path, tn) in ctx.Timelines)
                if (tn.GameId == gid && tn.StartUtc <= f.Name.StartUtc && (timeline is null || tn.StartUtc > best)) { timeline = path; best = tn.StartUtc; }
        }
        var markers = await CountMarkersAsync(timeline, offset, f.Name.StartUtc, media, ct).ConfigureAwait(false);
        return NewEntry(f, media, scan.SizeBytes, null, markers, offset);
    }

    private async Task<RecordingEntry> ClipEntryAsync(RecordingFolder f, CachedScan scan, SessionMedia media, CancellationToken ct)
    {
        var mediaName = Path.GetFileName(scan.MediaFolder ?? media.Folder);
        var pb = scan.Clip;
        var offset = pb?.OffsetMsForFolder(mediaName) ?? pb?.OffsetMs;
        string? timeline = null;
        try
        {
            timeline = Directory.EnumerateFiles(Path.Combine(f.FullPath, "timelines"), "*.json").Order(StringComparer.Ordinal).FirstOrDefault();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* no timeline */ }
        var bgStart = FolderNames.ParseMediaFolder(mediaName)?.StartUtc ?? f.Name.StartUtc;
        var markers = await CountMarkersAsync(timeline, offset, bgStart, media, ct).ConfigureAwait(false);
        var title = string.IsNullOrWhiteSpace(pb?.Title) ? null : pb!.Title;
        return NewEntry(f, media, scan.SizeBytes, title, markers, offset);
    }

    private static async Task<int> CountMarkersAsync(string? timelinePath, long? offset, DateTimeOffset bgStart, SessionMedia media, CancellationToken ct)
    {
        if (timelinePath is null) return 0;
        var file = await Timeline.ReadAsync(timelinePath, ct).ConfigureAwait(false);
        var start = FolderNames.ParseTimeline(Path.GetFileName(timelinePath))?.StartUtc;
        var set = Markers.Map(file, offset, bgStart, start);
        return Markers.Place(set.Markers, media).Visible.Count;
    }

    private static RecordingEntry NewEntry(RecordingFolder f, SessionMedia media, long size, string? title, int markers, long? offset) =>
        new(f.Kind, f.Name.GameId?.ToString(CultureInfo.InvariantCulture), f.Name.StartUtc, f.RelativeFolder, media.Runs, media.SegmentSeconds,
            media.DurationSeconds, media.Live, media.Temporary, title, markers, offset, size);

    // ------------------------------------------------------------------------------------------------- many folders

    /// <summary>
    /// Scans <paramref name="folders"/> with at most <paramref name="parallelism"/> folders at a time (each folder also has its own
    /// bounded head-read parallelism). <paramref name="onScanned"/> is called one at a time, so it needs no locking of its own.
    /// </summary>
    public async Task ScanFoldersAsync(IReadOnlyList<RecordingFolder> folders, Func<string, FolderHeads?> trusted, RecordingContext context,
        int parallelism, Func<FolderScan, ValueTask>? onScanned, CancellationToken ct = default)
    {
        using var serial = new SemaphoreSlim(1);
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Max(1, parallelism), CancellationToken = ct };
        await Parallel.ForEachAsync(folders, options, async (f, token) =>
        {
            var s = await ScanFolderAsync(f, trusted(f.RelativeFolder), context, token).ConfigureAwait(false);
            if (onScanned is null) return;
            await serial.WaitAsync(token).ConfigureAwait(false);
            try { await onScanned(s).ConfigureAwait(false); }
            finally { serial.Release(); }
        }).ConfigureAwait(false);
    }

    /// <summary>One complete pass without a service: enumerate, scan with <paramref name="cache"/>, assemble. Used by tools and the real-library test.</summary>
    public async Task<LibraryScan> ScanAllAsync(ScanCache? cache, Guid libraryId, int parallelism = 4, CancellationToken ct = default)
    {
        var sw = Stopwatch.StartNew();
        var folders = Enumerate();
        var ctx = await LoadContextAsync(ct).ConfigureAwait(false);
        var entries = new List<RecordingEntry>(folders.Count);
        var heads = new Dictionary<string, FolderHeads>(folders.Count, StringComparer.Ordinal);
        var reads = 0;
        await ScanFoldersAsync(folders, k => cache?.Get(k), ctx, parallelism, s =>
        {
            reads += s.HeadReads;
            if (s.Entry is { } e) entries.Add(e);
            if (!s.Failed) heads[s.Folder.RelativeFolder] = s.Heads;
            return ValueTask.CompletedTask;
        }, ct).ConfigureAwait(false);
        var index = await AssembleAsync(libraryId, entries, resolveNames: true, ct).ConfigureAwait(false);
        return new(index, ScanCache.From(heads), folders.Count, reads, sw.Elapsed);
    }

    // ---------------------------------------------------------------------------------------------------- assemble

    /// <summary>
    /// The index for <paramref name="entries"/>: games (name, art) for every game id used, recordings newest first. Names and art are
    /// resolved once per game and remembered; <paramref name="resolveNames"/> false only uses what is known (no reads of Steam's
    /// <c>appinfo.vdf</c>), for quick intermediate results during a long first scan.
    /// </summary>
    public async Task<LibraryIndex> AssembleAsync(Guid libraryId, IEnumerable<RecordingEntry> entries, bool resolveNames, CancellationToken ct = default)
    {
        var recordings = entries.OrderByDescending(e => e.StartUtc).ThenBy(e => e.RelativeFolder, StringComparer.Ordinal).ToList();
        var ids = recordings.Select(r => r.GameId).OfType<string>()
            .Select(s => ulong.TryParse(s, NumberStyles.None, CultureInfo.InvariantCulture, out var id) ? id : (ulong?)null)
            .OfType<ulong>().Distinct().ToList();

        if (resolveNames)
        {
            var missing = ids.Where(id => !_games.ContainsKey(id)).ToList();
            if (missing.Count > 0 && _names is not null)
            {
                var resolved = await _names.ResolveManyAsync(missing, ct).ConfigureAwait(false);
                foreach (var (id, n) in resolved)
                    _games[id] = new GameEntry(id.ToString(CultureInfo.InvariantCulture), n.Name, GameIds.IsShortcut(id));
            }
            if (_names is not null && Artwork is { } store)
            {
                foreach (var id in ids)
                {
                    if (!_artDone.Add(id)) continue;
                    var key = id.ToString(CultureInfo.InvariantCulture);
                    var src = _names.FindArt(id);
                    var art = await store.CopyAsync(key, new(src.Portrait ?? src.Header, src.Hero, src.HeroBlur, src.Logo), ct).ConfigureAwait(false);
                    var g = _games.GetValueOrDefault(id) ?? new GameEntry(key, GameNameResolver.Fallback(id), GameIds.IsShortcut(id));
                    _games[id] = g with { Art = art };
                }
            }
        }

        var games = new List<GameEntry>(ids.Count);
        foreach (var id in ids)
            games.Add(_games.TryGetValue(id, out var g) ? g
                : new GameEntry(id.ToString(CultureInfo.InvariantCulture), GameNameResolver.Fallback(id), GameIds.IsShortcut(id)));
        games.Sort(static (a, b) =>
        {
            var c = string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
            return c != 0 ? c : string.CompareOrdinal(a.GameId, b.GameId);
        });
        return LibraryIndex.Create(libraryId, _clock.GetUtcNow(), games, recordings);
    }
}
