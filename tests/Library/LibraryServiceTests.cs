// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Tests.Fakes;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class LibraryServiceTests
{
    private static readonly TimeSpan Window = TimeSpan.FromMilliseconds(600);
    private const string Bg = "bg_730_20250101_120000";
    private const string Clip = "clip_730_20250101_130000";

    private sealed class Rig : IAsyncDisposable
    {
        public SynthLibrary S { get; } = new();
        public FakeFolderWatcher Watcher { get; } = new();
        public List<(LibraryIndex Index, string[] Changed)> IndexEvents { get; } = [];
        public List<(string Folder, SessionMedia? Media)> MediaEvents { get; } = [];
        public int Reads;
        public int Saves;
        public LibraryService Service { get; private set; } = null!;

        public void Create(bool confirmed = true, string? root = "", Action<LibraryServiceOptions>? _ = null)
        {
            Service = new LibraryService(new LibraryServiceOptions
            {
                RecordingRoot = root == "" ? S.Root : root,
                DataFolder = confirmed ? S.Data : null,
                Names = S.Names,
                Clock = S.Clock,
                WatcherFactory = () => Watcher,
                Scan = new() { HeadReader = (p, ct) => { Interlocked.Increment(ref Reads); return ScanHeads.ReadAsync(p, ct); } },
                Saved = () => Interlocked.Increment(ref Saves),
            });
            Service.IndexChanged += (i, c) => { lock (IndexEvents) IndexEvents.Add((i, [.. c])); };
            Service.MediaChanged += (f, m) => { lock (MediaEvents) MediaEvents.Add((f, m)); };
        }

        public async Task StartAndSettleAsync()
        {
            await Service.StartAsync();
            await Service.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
        }

        public async Task SettleAsync() => await Service.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));

        public async ValueTask DisposeAsync()
        {
            if (Service is not null) await Service.DisposeAsync();
            S.Dispose();
        }
    }

    [Fact]
    public async Task Index_is_raised_from_disk_before_any_scan()
    {
        await using var r = new Rig();
        var rec = new RecordingEntry(RecordingKind.CacheSession, "730", new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), "video/" + Bg,
            [new(1, 5)], 3, 15, SizeBytes: 10);
        var index = LibraryIndex.Create(Guid.NewGuid(), r.S.Clock.GetUtcNow(), [new GameEntry("730", "Saved Name")], [rec]);
        await new IndexStore(r.S.Data).WriteAsync(index);
        r.Create();
        await r.Service.StartAsync();

        Assert.Equal(0, r.Reads);                                        // nothing was read from the recording folder yet
        var first = Assert.Single(r.IndexEvents.Take(1));
        Assert.Equal("Saved Name", Assert.Single(first.Index.Games).Name);
        Assert.Equal(["video/" + Bg], first.Changed);

        await r.SettleAsync();                                           // the folder is not on disk: the scan removes it
        Assert.Empty(r.Service.Index.Recordings);
        Assert.Equal(LibraryState.Ready, r.Service.Status.State);
    }

    [Fact]
    public async Task Warm_start_uses_the_scan_cache_and_does_not_read_heads()
    {
        await using (var r = new Rig())
        {
            r.S.AddBg(Bg, 1, 30);
            r.Create();
            await r.StartAndSettleAsync();
            Assert.Equal(60, r.Reads);
            await r.Service.FlushAsync();
            Assert.True(File.Exists(r.S.Data.ScanCacheJson));

            var warmReads = 0;
            var warm = new LibraryService(new LibraryServiceOptions
            {
                RecordingRoot = r.S.Root, DataFolder = r.S.Data, Names = r.S.Names, Clock = r.S.Clock, WatcherFactory = () => new FakeFolderWatcher(),
                Scan = new() { HeadReader = (p, ct) => { Interlocked.Increment(ref warmReads); return ScanHeads.ReadAsync(p, ct); } },
            });
            await warm.StartAsync();
            await warm.WhenIdleAsync().WaitAsync(TimeSpan.FromSeconds(30));
            Assert.Equal(0, warmReads);
            Assert.Equal(30, warm.Index.Recordings.Single().Runs.Single().Last);
            Assert.Equal("Test Game", warm.Index.Games.Single().Name);
            await warm.DisposeAsync();
        }
    }

    [Fact]
    public async Task New_clip_folder_appears_in_IndexChanged()
    {
        await using var r = new Rig();
        r.Create();
        await r.StartAndSettleAsync();
        Assert.Empty(r.Service.Index.Recordings);
        r.IndexEvents.Clear();

        var clipDir = r.S.AddClip(Clip, "bg_730_20250101_130000", 1, 4);
        r.Watcher.Raise(FolderChangeKind.Created, clipDir);
        r.Watcher.Raise(FolderChangeKind.Created, Path.Combine(clipDir, "video", "bg_730_20250101_130000", "init-stream0.m4s"));
        r.S.Clock.Advance(Window);
        await r.SettleAsync();

        var ev = Assert.Single(r.IndexEvents);
        Assert.Equal(["clips/" + Clip], ev.Changed);
        var rec = Assert.Single(ev.Index.Recordings);
        Assert.Equal(RecordingKind.Clip, rec.Kind);
        Assert.False(rec.Temporary);
        Assert.Equal([new SegmentRun(1, 4)], rec.Runs);
        Assert.Equal("Test Game", Assert.Single(ev.Index.Games).Name);
    }

    [Fact]
    public async Task Growing_bg_folder_raises_MediaChanged_once_per_debounce_window()
    {
        await using var r = new Rig();
        var dir = r.S.AddBg(Bg, 1, 3);
        r.Create();
        await r.StartAndSettleAsync();
        r.MediaEvents.Clear();

        // a burst of events (tmp growth, rename, mpd rewrite) inside one window: one rescan
        r.S.AddChunk(dir, 4);
        for (var i = 0; i < 6; i++)
        {
            r.Watcher.Raise(FolderChangeKind.Changed, Path.Combine(dir, "chunk-stream0-00004.m4s.tmp"));
            r.Watcher.Raise(FolderChangeKind.Renamed, Path.Combine(dir, "chunk-stream0-00004.m4s"), Path.Combine(dir, "chunk-stream0-00004.m4s.tmp"));
            r.S.Clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        Assert.Empty(r.MediaEvents);                                     // still inside the quiet window
        r.S.Clock.Advance(Window);
        await r.SettleAsync();
        var one = Assert.Single(r.MediaEvents);
        Assert.Equal("video/" + Bg, one.Folder);
        Assert.Equal(4, one.Media!.LastChunk);

        // the next chunk, 3 s later, is the next window
        r.S.AddChunk(dir, 5);
        r.Watcher.Raise(FolderChangeKind.Renamed, Path.Combine(dir, "chunk-stream1-00005.m4s"), Path.Combine(dir, "chunk-stream1-00005.m4s.tmp"));
        r.S.Clock.Advance(Window);
        await r.SettleAsync();
        Assert.Equal(2, r.MediaEvents.Count);
        Assert.Equal(5, r.Service.GetMedia("video/" + Bg)!.LastChunk);

        // nothing changed on disk: an event gives a rescan but no MediaChanged
        r.Watcher.Raise(FolderChangeKind.Created, Path.Combine(dir, "session.mpd"));
        r.S.Clock.Advance(Window);
        await r.SettleAsync();
        Assert.Equal(2, r.MediaEvents.Count);
    }

    [Fact]
    public async Task Continuous_events_still_rescan_within_the_max_debounce()
    {
        await using var r = new Rig();
        var dir = r.S.AddBg(Bg, 1, 3);
        r.Create();
        await r.StartAndSettleAsync();
        r.MediaEvents.Clear();
        r.S.AddChunk(dir, 4);
        for (var i = 0; i < 40; i++)                                     // an event every 100 ms for 4 s: the quiet window never comes
        {
            r.Watcher.Raise(FolderChangeKind.Created, Path.Combine(dir, "session.mpd"));
            r.S.Clock.Advance(TimeSpan.FromMilliseconds(100));
        }
        await r.SettleAsync();
        Assert.NotEmpty(r.MediaEvents);
    }

    [Fact]
    public async Task Deleted_folder_is_removed_from_the_index()
    {
        await using var r = new Rig();
        var a = r.S.AddBg(Bg, 1, 3);
        r.S.AddBg("bg_730_20250101_140000", 1, 3);
        r.Create();
        await r.StartAndSettleAsync();
        Assert.Equal(2, r.Service.Index.Recordings.Count);
        r.IndexEvents.Clear();
        r.MediaEvents.Clear();

        Directory.Delete(a, true);                                       // Steam trims a whole old session
        r.Watcher.Raise(FolderChangeKind.Deleted, a);
        r.S.Clock.Advance(Window);
        await r.SettleAsync();

        Assert.Equal(["video/bg_730_20250101_140000"], r.Service.Index.Recordings.Select(x => x.RelativeFolder));
        Assert.Contains("video/" + Bg, r.IndexEvents.Single().Changed);
        Assert.Equal(("video/" + Bg, (SessionMedia?)null), r.MediaEvents.Single());
    }

    [Fact]
    public async Task Watcher_overflow_triggers_a_full_rescan()
    {
        await using var r = new Rig();
        r.Create();
        await r.StartAndSettleAsync();
        r.S.AddBg(Bg, 1, 3);                                             // no event for it: the "lost" notifications
        r.S.AddClip(Clip, "bg_730_20250101_130000", 1, 2);
        Assert.Empty(r.Service.Index.Recordings);

        r.Watcher.Fail(new InternalBufferOverflowException());
        await r.SettleAsync();
        Assert.Equal(2, r.Service.Index.Recordings.Count);
        Assert.False(r.Watcher.Disposed);                                // overflow keeps the watcher
    }

    [Fact]
    public async Task Dead_watcher_is_replaced_by_the_full_rescan()
    {
        await using var r = new Rig();
        r.Create();
        await r.StartAndSettleAsync();
        r.Watcher.Fail(new IOException("network name deleted"));
        await r.SettleAsync();
        Assert.True(r.Watcher.Disposed);
        Assert.Equal(LibraryState.Ready, r.Service.Status.State);
    }

    [Fact]
    public async Task Nothing_is_ever_written_under_the_recording_root()
    {
        await using var r = new Rig();
        var dir = r.S.AddBg(Bg, 1, 6);
        r.S.AddClip(Clip, "bg_730_20250101_130000", 1, 3);
        var before = SynthLibrary.Snapshot(r.S.Root);
        r.Create();
        await r.StartAndSettleAsync();
        r.Watcher.Raise(FolderChangeKind.Changed, Path.Combine(dir, "session.mpd"));
        r.S.Clock.Advance(Window);
        await r.SettleAsync();
        r.S.Clock.Advance(TimeSpan.FromSeconds(10));
        await r.SettleAsync();
        await r.Service.FlushAsync();
        Assert.Equal(before, SynthLibrary.Snapshot(r.S.Root));
        Assert.True(File.Exists(r.S.Data.LibraryJson));                  // it all went to the data folder
    }

    [Fact]
    public async Task Unconfirmed_data_folder_works_in_memory_and_saves_nothing_until_confirmed()
    {
        await using var r = new Rig();
        r.S.AddBg(Bg, 1, 3);
        r.Create(confirmed: false);
        await r.StartAndSettleAsync();
        Assert.Single(r.Service.Index.Recordings);
        Assert.False(r.Service.Status.DataFolderConfirmed);
        r.S.Clock.Advance(TimeSpan.FromMinutes(1));
        await r.Service.FlushAsync();
        Assert.Equal(0, r.Saves);
        Assert.False(Directory.Exists(r.S.DataPath));

        r.Service.AttachDataFolder(r.S.Data);
        await r.SettleAsync();
        await r.Service.FlushAsync();
        Assert.True(r.Service.Status.DataFolderConfirmed);
        Assert.True(File.Exists(r.S.Data.LibraryJson));
        Assert.True(File.Exists(r.S.Data.ScanCacheJson));
        Assert.Contains("Test Game", await File.ReadAllTextAsync(r.S.Data.LibraryJson));
    }

    [Fact]
    public async Task Saves_are_coalesced_to_one_per_interval()
    {
        await using var r = new Rig();
        var dir = r.S.AddBg(Bg, 1, 3);
        r.Create();
        await r.StartAndSettleAsync();
        Assert.Equal(1, r.Saves);                                        // the first pass saves at once

        for (var n = 4; n <= 7; n++)                                     // four chunk windows inside 5 s
        {
            r.S.AddChunk(dir, n);
            r.Watcher.Raise(FolderChangeKind.Renamed, Path.Combine(dir, FolderNames.ChunkFile(0, n)), Path.Combine(dir, FolderNames.ChunkFile(0, n) + ".tmp"));
            r.S.Clock.Advance(Window);
            await r.SettleAsync();
        }
        Assert.Equal(1, r.Saves);
        r.S.Clock.Advance(TimeSpan.FromSeconds(5));
        await r.SettleAsync();
        Assert.Equal(2, r.Saves);
        var saved = (await new IndexStore(r.S.Data).ReadAsync()).Index!;
        Assert.Equal(7, saved.Recordings.Single().Runs.Single().Last);
    }

    [Fact]
    public async Task Live_session_stops_being_live_when_no_chunk_arrives()
    {
        await using var r = new Rig();
        var recent = r.S.Clock.GetUtcNow().UtcDateTime.AddSeconds(-3);
        var dir = r.S.AddBg(Bg, 1, 3, recent);
        r.S.WriteMpd(dir, "dynamic", recent);
        r.Create();
        await r.StartAndSettleAsync();
        Assert.True(r.Service.Index.Recordings.Single().Live);
        r.IndexEvents.Clear();

        r.S.Clock.Advance(TimeSpan.FromSeconds(20));                    // the one-shot timer; no event ever told us
        await r.SettleAsync();
        Assert.False(r.Service.Index.Recordings.Single().Live);
        Assert.Single(r.IndexEvents);
    }

    [Fact]
    public async Task Vanishing_and_unreadable_files_do_not_break_the_pass()
    {
        await using var r = new Rig();
        var dir = r.S.AddBg(Bg, 1, 5);
        r.S.AddBg("bg_730_20250101_150000", 1, 2);
        r.Create();
        await r.Service.StartAsync();
        File.Delete(Path.Combine(dir, FolderNames.ChunkFile(0, 2)));    // trimmed while scanning
        await r.SettleAsync();
        Assert.Equal(2, r.Service.Index.Recordings.Count);
        await using var hold = new FileStream(Path.Combine(dir, FolderNames.ChunkFile(0, 4)), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        r.Service.RequestFolderRescan("video/" + Bg);                    // a locked chunk is an issue, not an exception
        await r.SettleAsync();
        Assert.Equal(2, r.Service.Index.Recordings.Count);
    }

    [Fact]
    public async Task No_steam_gives_an_empty_library_and_a_status()
    {
        await using var r = new Rig();
        r.Create(root: null);
        await r.Service.StartAsync();
        Assert.Equal(LibraryState.NoSteam, r.Service.Status.State);
        Assert.Empty(r.Service.Index.Recordings);
        await r.Service.WhenIdleAsync();
        Assert.Single(r.IndexEvents);
    }

    [Fact]
    public async Task Unreachable_recording_folder_keeps_the_saved_index()
    {
        await using var r = new Rig();
        var rec = new RecordingEntry(RecordingKind.CacheSession, "730", new(2025, 1, 1, 12, 0, 0, TimeSpan.Zero), "video/" + Bg, [new(1, 5)]);
        await new IndexStore(r.S.Data).WriteAsync(LibraryIndex.Create(Guid.NewGuid(), r.S.Clock.GetUtcNow(), [], [rec]));
        Directory.Delete(r.S.Root, true);                                // drive not mounted
        r.Create();
        await r.StartAndSettleAsync();
        Assert.Equal(LibraryState.RecordingFolderMissing, r.Service.Status.State);
        Assert.Single(r.Service.Index.Recordings);
        Assert.Equal(0, r.Saves);
    }

    [Fact]
    public async Task Progress_reports_folders_done_of_total()
    {
        await using var r = new Rig();
        for (var i = 0; i < 3; i++) r.S.AddBg($"bg_730_2025010{i + 1}_120000", 1, 2);
        r.Create();
        var progress = new List<ScanProgress>();
        r.Service.ProgressChanged += p => { lock (progress) progress.Add(p); };
        await r.StartAndSettleAsync();
        Assert.Contains(progress, p => p is { Phase: LibraryPhase.Scanning, FoldersDone: 3, FoldersTotal: 3 });
        Assert.Equal(LibraryPhase.Idle, progress[^1].Phase);
    }

    [Fact]
    public async Task Index_from_cached_files_is_visible_within_the_budget_for_200_recordings()
    {
        await using var r = new Rig();
        var recs = new List<RecordingEntry>();
        var heads = new List<KeyValuePair<string, FolderHeads>>();
        for (var i = 0; i < 200; i++)
        {
            var name = $"bg_730_{20250101 + i % 28:00000000}_{120000 + i:000000}";
            var rel = "video/" + name;
            recs.Add(new(RecordingKind.CacheSession, "730", new DateTimeOffset(2025, 1, 1, 12, 0, 0, TimeSpan.Zero).AddSeconds(i), rel, [new(1, 40)], 3, 120, SizeBytes: 1 << 20));
            var json = "{\"version\":1,\"folders\":{\"" + rel + "\":{\"chunks\":[" +
                string.Join(",", Enumerable.Range(1, 40).SelectMany(n => new[] { $"[0,{n},40,{n},1]", $"[1,{n},40,{n},1]" })) + "]}}}";
            heads.Add(new(rel, ScanCache.Parse(System.Text.Encoding.UTF8.GetBytes(json)).Cache.Get(rel)!));
        }
        await new IndexStore(r.S.Data).WriteAsync(LibraryIndex.Create(Guid.NewGuid(), r.S.Clock.GetUtcNow(), [new GameEntry("730", "Test Game")], recs));
        await new ScanCacheStore(r.S.Data).WriteAsync(ScanCache.From(heads));
        r.Create();

        var sw = Stopwatch.StartNew();
        TimeSpan? first = null;
        r.Service.IndexChanged += (i, _) => first ??= sw.Elapsed;
        await r.Service.StartAsync();
        Assert.NotNull(first);
        Assert.Equal(200, r.IndexEvents[0].Index.Recordings.Count);
        Assert.True(first < TimeSpan.FromMilliseconds(400), $"index visible after {first!.Value.TotalMilliseconds:F0} ms");
    }

    [Fact]
    public async Task Real_file_system_watcher_sees_a_new_chunk()
    {
        using var s = new SynthLibrary();
        var dir = s.AddBg(Bg, 1, 3);
        using var w = new FileSystemFolderWatcher();
        var seen = new TaskCompletionSource<FolderChange>(TaskCreationOptions.RunContinuationsAsynchronously);
        w.Changed += c => { if (c.FullPath.EndsWith("00004.m4s", StringComparison.Ordinal)) seen.TrySetResult(c); };
        w.Start(s.Root);
        s.AddChunk(dir, 4);
        var c = await seen.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.StartsWith(s.Root, c.FullPath, StringComparison.OrdinalIgnoreCase);
    }
}
