// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;
using Xunit.Abstractions;

namespace SteamPoweredPlayer.Tests.RealLibrary;

/// <summary>
/// Opt-in, strictly read-only checks against a real library: set SPP_REAL_LIBRARY to a BackgroundRecordPath. Without it every test
/// returns at once. Output is aggregate counts only (no names, paths or ids), so a run can be pasted into an issue.
/// </summary>
public sealed class RealLibraryTests(ITestOutputHelper output)
{
    private static string? Root
    {
        get
        {
            var r = Environment.GetEnvironmentVariable("SPP_REAL_LIBRARY");
            return string.IsNullOrWhiteSpace(r) || !Directory.Exists(r) ? null : r;
        }
    }

    internal static async Task<byte[]?> ReadSharedAsync(string path, CancellationToken ct = default)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 1 << 16, true);
            var buf = new byte[fs.Length];
            await fs.ReadExactlyAsync(buf, ct);
            return buf;
        }
        catch (IOException) { return null; }               // Steam may trim while we read
        catch (UnauthorizedAccessException) { return null; }
    }

    private static IEnumerable<string> CacheSessions(string root) =>
        Directory.Exists(Path.Combine(root, "video")) ? Directory.EnumerateDirectories(Path.Combine(root, "video"), "bg_*") : [];

    private static IEnumerable<string> Clips(string root) =>
        Directory.Exists(Path.Combine(root, "clips")) ? Directory.EnumerateDirectories(Path.Combine(root, "clips"), "clip_*") : [];

    [Fact]
    public async Task Every_recording_folder_name_parses_and_scans()
    {
        if (Root is not { } root) return;
        var status = new Dictionary<string, int>();
        var issues = new Dictionary<string, int>();
        int folders = 0, unknownNames = 0, crashed = 0, live = 0, unknownFiles = 0, chunks = 0, multiRun = 0;

        async Task One(string folder, bool clip)
        {
            folders++;
            if (FolderNames.ParseRecording(Path.GetFileName(folder)) is null) unknownNames++;
            var r = clip ? await Scanner.ScanClipAsync(folder) : await Scanner.ScanAsync(folder);
            Bump(status, (clip ? "clip:" : "cache:") + r.Status);
            foreach (var i in r.Issues) Bump(issues, i.Kind.ToString());
            unknownFiles += r.UnknownFiles.Count;
            if (r.Crashed) crashed++;
            if (r.Media is { } m) { chunks += m.ChunkCount; if (m.Runs.Count > 1) multiRun++; if (m.Live) live++; }
        }

        foreach (var f in CacheSessions(root)) await One(f, clip: false);
        foreach (var f in Clips(root)) await One(f, clip: true);

        output.WriteLine($"folders {folders}, unknown names {unknownNames}, chunks {chunks}, multi-run {multiRun}, live {live}, crashed {crashed}, unknown files {unknownFiles}");
        output.WriteLine("status: " + Fmt(status));
        output.WriteLine("issues: " + Fmt(issues));
        Assert.True(folders > 0, "no recording folders found");
        Assert.Equal(0, unknownNames);
        Assert.Contains(status.Keys, k => k.EndsWith(nameof(ScanStatus.Playable), StringComparison.Ordinal));
    }

    [Fact]
    public async Task Every_timeline_parses_and_marker_units_look_right()
    {
        if (Root is not { } root) return;
        var dir = Path.Combine(root, "timelines");
        if (!Directory.Exists(dir)) return;
        int files = 0, badNames = 0, withDiag = 0, entries = 0, unknownTypes = 0, rangeEntries = 0;
        var diagSamples = new List<string>();
        var durations = new List<long>();
        foreach (var path in Directory.EnumerateFiles(dir, "timeline_*.json"))
        {
            files++;
            var name = FolderNames.ParseTimeline(Path.GetFileName(path));
            if (name is null) badNames++;
            if (await ReadSharedAsync(path) is not { } bytes) continue;
            var t = Timeline.Parse(bytes);
            if (t.Diagnostics.Count > 0) { withDiag++; if (diagSamples.Count < 5) diagSamples.Add(t.Diagnostics[0]); }
            if (name is not null && t.DateRecordedUtc is { } d) Assert.Equal(name.StartUtc, d);   // format doc: daterecorded == name
            entries += t.Entries.Count;
            unknownTypes += t.Entries.Count(e => e.Type == TimelineEntryType.Unknown);
            foreach (var e in t.Entries.Where(e => e.DurationMs > 0)) { rangeEntries++; durations.Add(e.DurationMs); }
        }
        durations.Sort();
        output.WriteLine($"timelines {files}, bad names {badNames}, with diagnostics {withDiag}, entries {entries}, unknown types {unknownTypes}, ranges {rangeEntries}");
        if (durations.Count > 0)
            output.WriteLine($"range durations: min {durations[0]}, median {durations[durations.Count / 2]}, max {durations[^1]} (ms assumed)");
        foreach (var s in diagSamples) output.WriteLine("diag: " + s);
        Assert.Equal(0, badNames);
    }

    [Fact]
    public async Task Markers_of_cache_sessions_land_inside_the_media()
    {
        if (Root is not { } root) return;
        if (await ReadSharedAsync(Path.Combine(root, "gamerecording.pb")) is not { } pbBytes) return;
        var pb = RecordingPb.ParseGameRecording(pbBytes);
        var timelines = Directory.Exists(Path.Combine(root, "timelines"))
            ? Directory.EnumerateFiles(Path.Combine(root, "timelines"), "timeline_*.json")
                .Select(p => (Path: p, Name: FolderNames.ParseTimeline(Path.GetFileName(p))))
                .Where(x => x.Name is not null).ToList()
            : [];
        int sessions = 0, exact = 0, fallback = 0, none = 0, visible = 0, trimmed = 0;
        var reasons = new Dictionary<string, int>();
        foreach (var folder in CacheSessions(root))
        {
            var bg = FolderNames.ParseRecording(Path.GetFileName(folder));
            if (bg?.GameId is not { } gid) continue;
            var scan = await Scanner.ScanAsync(folder);
            if (scan.Media is not { } media) continue;
            sessions++;
            // The timeline of a session: same game, latest start at or before the bg start (a timeline can start ~113 s earlier).
            var tl = timelines.Where(x => x.Name!.GameId == gid && x.Name.StartUtc <= bg.StartUtc)
                              .OrderByDescending(x => x.Name!.StartUtc).FirstOrDefault();
            if (tl.Path is null || await ReadSharedAsync(tl.Path) is not { } bytes) { none++; continue; }
            var offset = pb.OffsetMsForFolder(Path.GetFileName(folder));
            if (offset is null) fallback++; else exact++;
            var set = Markers.Map(Timeline.Parse(bytes), offset, bg.StartUtc, tl.Name!.StartUtc);
            var placed = Markers.Place(set.Markers, media);
            visible += placed.Visible.Count;
            trimmed += placed.TrimmedAway.Count;
            foreach (var t in placed.TrimmedAway) Bump(reasons, t.Reason.ToString());
        }
        output.WriteLine($"cache sessions {sessions}: exact offset {exact}, fallback {fallback}, no timeline {none}; markers visible {visible}, trimmed away {trimmed} ({Fmt(reasons)})");
        output.WriteLine($"gamerecording.pb: timelines {pb.Timelines.Count}, with session {pb.Timelines.Count(t => t.Session is not null)}, tag index {pb.TagIndex.Count}, diagnostics {pb.Diagnostics.Count}");
    }

    [Fact]
    public async Task Steam_config_points_at_this_library_and_every_game_gets_a_name()
    {
        if (Root is not { } root) return;
        if (SteamPaths.FromRegistry() is not { } steam) return;            // Steam not installed on this machine
        var user = await steam.ReadActiveUserAsync(default);
        Assert.NotNull(user);
        var config = await SteamConfig.LoadAsync(steam.LocalConfig(user.AccountId), default);
        output.WriteLine($"config: mode {config.BackgroundRecordMode}, max keep {config.BackgroundMaxKeep}, instant clip {config.InstantClipDuration}, per-game entries {config.PerGameEnabled.Count}, diagnostics {config.Diagnostics.Count}");
        foreach (var d in config.Diagnostics) output.WriteLine("config diag: " + d);
        Assert.NotNull(config.BackgroundMaxKeep);
        Assert.Equal(Path.GetFullPath(root).TrimEnd('\\', '/'), Path.GetFullPath(config.BackgroundRecordPath!).TrimEnd('\\', '/'), ignoreCase: true);

        var ids = CacheSessions(root).Concat(Clips(root))
            .Select(f => FolderNames.ParseRecording(Path.GetFileName(f))?.GameId).OfType<ulong>().Distinct().ToList();
        var names = await new GameNameResolver(steam, user.AccountId).ResolveManyAsync(ids, default);
        var bySource = names.Values.GroupBy(n => n.Source.ToString()).ToDictionary(g => g.Key, g => g.Count());
        int withArt = ids.Count(id => new GameNameResolver(steam, user.AccountId).FindArt(id) is { } a && (a.Portrait ?? a.Hero ?? a.Logo) is not null);
        output.WriteLine($"games {ids.Count}, shortcuts {ids.Count(GameIds.IsShortcut)}, names by source: {Fmt(bySource)}, with art {withArt}");
        Assert.Equal(ids.Count, names.Count);
    }

    [Fact]
    public async Task Recordings_per_timeline_and_recording_types()
    {
        // Steam's own schema (SteamDatabase/Protobufs webui/common.proto): timeline field 5 = repeated recordings
        // {1 recording_id, 2 start_offset_ms, 3 duration_ms, 4 recording_type, 10 recording_zero_timeline_offset_ms}.
        if (Root is not { } root) return;
        var types = new Dictionary<string, int>();
        int timelines = 0, multi = 0, clips = 0, clipMultiTimeline = 0, clipFolderMatches = 0, clipFolderMisses = 0;
        void Count(PbMessage tl)
        {
            timelines++;
            var recs = tl.All(5).ToList();
            if (recs.Count > 1) multi++;
            foreach (var r in recs)
            {
                var m = PbMessage.Parse(r.Payload);
                var id = m.First(1)?.AsString() ?? "?";
                var prefix = id.StartsWith("fg_", StringComparison.Ordinal) ? "fg" : id.Count(c => c == '_') > 3 ? "bg_n" : "bg";
                Bump(types, $"{prefix}:type{m.First(4)?.AsInt64()}");
            }
        }
        if (await ReadSharedAsync(Path.Combine(root, "gamerecording.pb")) is { } g)
            foreach (var f in PbMessage.Parse(g).All(1)) Count(PbMessage.Parse(f.Payload));
        foreach (var clip in Clips(root))
        {
            if (await ReadSharedAsync(Path.Combine(clip, "clip.pb")) is not { } c) continue;
            clips++;
            var raw = PbMessage.Parse(c);
            var tls = raw.All(1).Select(f => PbMessage.Parse(f.Payload)).ToList();
            if (tls.Count > 1) clipMultiTimeline++;
            foreach (var t in tls) Count(t);
            var media = Scanner.FindClipMediaFolder(clip) is { } mf ? Path.GetFileName(mf) : null;
            var ids = tls.SelectMany(t => t.All(5)).Select(r => PbMessage.Parse(r.Payload).First(1)?.AsString()).ToHashSet();
            if (media is not null && ids.Contains(media)) clipFolderMatches++; else clipFolderMisses++;
        }
        output.WriteLine($"timelines {timelines}, with >1 recordings {multi}; clips {clips}, with >1 timelines {clipMultiTimeline}; clip media folder = a recording_id: {clipFolderMatches} yes, {clipFolderMisses} no");
        output.WriteLine("recording_type by folder kind: " + Fmt(types));
    }

    [Fact]
    public async Task Library_builder_cold_then_warm_pass_reads_no_heads_the_second_time()
    {
        if (Root is not { } root) return;
        var temp = Path.Combine(Path.GetTempPath(), "spp-real-" + Guid.NewGuid().ToString("N"));
        try
        {
            // The data folder is a temp folder outside the recording root; the recording root is only read.
            var data = DataFolder.Open(temp, root);
            var steam = SteamPaths.FromRegistry();
            var user = steam is null ? null : await steam.ReadActiveUserAsync(default);
            var names = steam is null ? null : new GameNameResolver(steam, user?.AccountId);
            var reads = 0;
            var scan = new ScanOptions { HeadReader = (p, ct) => { Interlocked.Increment(ref reads); return ScanHeads.ReadAsync(p, ct); } };
            var builder = new LibraryBuilder(root, names, new ArtworkStore(data), scan: scan);
            var id = Guid.NewGuid();

            var cold = await builder.ScanAllAsync(null, id, parallelism: 4);
            await new ScanCacheStore(data).WriteAsync(cold.Cache);
            var coldReads = reads;
            reads = 0;

            var loaded = (await new ScanCacheStore(data).ReadAsync()).Cache;
            var warm = await new LibraryBuilder(root, names, null, scan: scan).ScanAllAsync(loaded, id, parallelism: 4);
            output.WriteLine($"folders {cold.Folders}, recordings {cold.Index.Recordings.Count}, games {cold.Index.Games.Count}, cache folders {loaded.Folders.Count}");
            output.WriteLine($"cold: {cold.Elapsed.TotalSeconds:F1} s, head reads {coldReads}; warm: {warm.Elapsed.TotalSeconds:F1} s, head reads {reads}");
            output.WriteLine($"markers in index {cold.Index.Recordings.Sum(r => r.MarkerCount)}, with exact offset {cold.Index.Recordings.Count(r => r.ExactOffsetMs is not null)}");
            Assert.True(reads <= Math.Max(10, coldReads / 50), $"warm pass read {reads} heads");      // a few new chunks may appear in between
            Assert.InRange(Math.Abs(warm.Index.Recordings.Count - cold.Index.Recordings.Count), 0, Math.Max(3, cold.Folders / 20));
        }
        finally
        {
            try { Directory.Delete(temp, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }

    private static void Bump(Dictionary<string, int> d, string k) => d[k] = d.GetValueOrDefault(k) + 1;
    private static string Fmt(Dictionary<string, int> d) => d.Count == 0 ? "none" : string.Join(", ", d.OrderBy(k => k.Key).Select(k => $"{k.Key} {k.Value}"));
}
