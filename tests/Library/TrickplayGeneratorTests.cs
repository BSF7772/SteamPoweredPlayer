// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core;
using System.Diagnostics;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;
using Xunit.Abstractions;

namespace SteamPoweredPlayer.Tests.Library;

/// <summary>A synthetic Steam-style DASH session (ffmpeg's dash muxer, Steam's file names, 3 s segments), generated once per test class.</summary>
public sealed class SyntheticSession : IDisposable
{
    public const int Chunks = 105;
    private readonly TempDir _dir = new();
    public string Folder { get; }
    public bool Available { get; }
    public string? Error { get; }

    public SyntheticSession()
    {
        Folder = _dir.Sub("session");
        Directory.CreateDirectory(Folder);
        Available = Trickplay.FfmpegAvailable();
        if (!Available) return;
        var psi = Ff.StartInfo(Ff.Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", "testsrc2=size=320x180:rate=10",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", (Chunks * 3).ToString(System.Globalization.CultureInfo.InvariantCulture),
            "-map", "0:v", "-map", "1:a",
            "-c:v", "libx264", "-preset", "ultrafast", "-pix_fmt", "yuv420p", "-sc_threshold", "0",
            "-force_key_frames", "expr:gte(t,n_forced*3)", "-c:a", "aac", "-b:a", "32k",
            "-f", "dash", "-seg_duration", "3", "-use_template", "1", "-use_timeline", "1",
            "-init_seg_name", "init-stream$RepresentationID$.m4s",
            "-media_seg_name", "chunk-stream$RepresentationID$-$Number%05d$.m4s",
            "session.mpd",
        ]);
        psi.WorkingDirectory = Folder; // the dash muxer writes the segments relative to the working directory
        using var p = Process.Start(psi)!;
        var err = p.StandardError.ReadToEnd();
        p.WaitForExit();
        if (p.ExitCode != 0) Error = err;
    }

    /// <summary>Copies init + chunks 1..<paramref name="count"/> of the video stream to a fresh folder.</summary>
    public string CopyTo(string dest, int count)
    {
        Directory.CreateDirectory(dest);
        File.Copy(Path.Combine(Folder, FolderNames.InitFile(0)), Path.Combine(dest, FolderNames.InitFile(0)));
        for (var c = 1; c <= count; c++)
            File.Copy(Path.Combine(Folder, FolderNames.ChunkFile(0, c)), Path.Combine(dest, FolderNames.ChunkFile(0, c)));
        return dest;
    }

    public void Dispose() => _dir.Dispose();
}

/// <summary>Runs alone: the ffmpeg encode would otherwise starve the timing-budget tests of other classes.</summary>
[CollectionDefinition("ffmpeg", DisableParallelization = true)]
public sealed class FfmpegCollection;

[Collection("ffmpeg")]
public sealed class TrickplayGeneratorTests(SyntheticSession session, ITestOutputHelper output) : IClassFixture<SyntheticSession>
{
    private static SessionMedia Media(string folder, int first, int last) =>
        new(folder, [new SegmentRun(first, last)], SessionMedia.DefaultSegmentSeconds, Live: false, Temporary: true);

    private bool CanRun()
    {
        if (!session.Available) { output.WriteLine("ffmpeg not found: test skipped"); return false; }
        Assert.Null(session.Error);
        return true;
    }

    private static DataFolder NewData(TempDir t) => DataFolder.Open(t.Sub("data"), t.Sub("steam", "rec"));

    private static readonly TrickplayOptions Cpu = new(DecodeMode.Cpu, TileWidth: 160);   // the 320x180 synthetic source is never upscaled

    [Fact]
    public async Task Generates_one_sheet_per_hundred_tiles_with_all_chunks()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var data = NewData(t);
        var media = Media(session.Folder, 1, SyntheticSession.Chunks);
        var sw = Stopwatch.StartNew();
        var r = await Trickplay.GenerateAsync(data, "bg_1_test", media, Cpu);
        sw.Stop();
        output.WriteLine($"{SyntheticSession.Chunks} chunks in {sw.ElapsedMilliseconds} ms = {(double)sw.ElapsedMilliseconds / SyntheticSession.Chunks:F1} ms/chunk (CPU, 320x180 H.264, incl. ffmpeg start)");

        Assert.Equal(SyntheticSession.Chunks, r.NewChunks);
        Assert.Equal(0, r.Rebuilt);
        Assert.Equal(2, r.SheetsWritten);
        Assert.Empty(r.Missing);
        Assert.Equal(DecodeMode.Cpu, r.UsedMode);

        var dir = Trickplay.DirFor(data, "bg_1_test");
        var m = TrickplayManifest.TryRead(dir)!;
        Assert.Equal([100, 5], m.Sheets.Select(s => s.Chunks.Count));
        Assert.Equal(Enumerable.Range(1, SyntheticSession.Chunks), m.Sheets.SelectMany(s => s.Chunks));
        Assert.Equal((160, 90, 10, 10), (m.TileWidth, m.TileHeight, m.Columns, m.Rows));
        Assert.Equal(3.0, m.IntervalSeconds);

        foreach (var s in m.Sheets)
        {
            var bytes = await File.ReadAllBytesAsync(Path.Combine(dir, s.File));
            Assert.True(Trickplay.TryGetJpegSize(bytes, out var w, out var h));
            Assert.Equal((1600, 900), (w, h)); // partial sheets are padded to the full grid
        }
        Assert.Equal(["manifest.json", "sheet-0000.jpg", "sheet-0001.jpg"], Directory.GetFileSystemEntries(dir).Select(Path.GetFileName).Order());

        var idx = TrickplayIndex.TryLoad(data, "bg_1_test")!;
        Assert.Equal(SyntheticSession.Chunks, idx.Count);
        var tile = idx.Find(100 * 3.0 + 1)!; // chunk 101 -> second sheet, first tile
        Assert.EndsWith("sheet-0001.jpg", tile.SheetPath);
        Assert.Equal((0, 0), (tile.X, tile.Y));
    }

    [Fact]
    public async Task Sheets_made_with_another_tile_width_are_regenerated()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var data = NewData(t);
        var media = Media(session.Folder, 1, 12);
        await Trickplay.GenerateAsync(data, "k", media, Cpu with { TileWidth = 96 });
        var r = await Trickplay.GenerateAsync(data, "k", media, Cpu);
        Assert.Equal(12, r.NewChunks);
        Assert.Equal(160, TrickplayManifest.TryRead(Trickplay.DirFor(data, "k"))!.TileWidth);
    }

    [Fact]
    public async Task A_small_session_makes_one_partial_sheet_in_chunk_order()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var data = NewData(t);
        await Trickplay.GenerateAsync(data, "k", Media(session.Folder, 1, 12), Cpu);
        var dir = Trickplay.DirFor(data, "k");
        var sheet = Path.Combine(dir, "sheet-0000.jpg");
        Assert.True(new FileInfo(sheet).Length > 5_000);
        Assert.Equal(12, TrickplayManifest.TryRead(dir)!.Sheets[0].Chunks.Count);
    }

    [Fact]
    public async Task Zero_filled_and_vanished_chunks_are_skipped_and_reported()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var folder = session.CopyTo(t.Sub("steam", "video", "bg_test"), 8);
        var zero = Path.Combine(folder, FolderNames.ChunkFile(0, 3));
        File.WriteAllBytes(zero, new byte[new FileInfo(zero).Length]);
        var truncated = Path.Combine(folder, FolderNames.ChunkFile(0, 6));
        File.WriteAllBytes(truncated, File.ReadAllBytes(truncated).AsSpan(0, 20).ToArray()); // cut inside the moof
        var gone = Path.Combine(folder, FolderNames.ChunkFile(0, 5));
        File.Delete(gone); // Steam trimmed it after the scan (test's own copy)

        var data = NewData(t);
        var progress = new List<TrickplayProgress>();
        var r = await Trickplay.GenerateAsync(data, "k", Media(folder, 1, 8), Cpu, new SyncProgress(progress.Add));

        Assert.Equal(5, r.NewChunks);
        Assert.Equal(
            [new MissingChunk(3, MissingReason.ZeroFilled), new MissingChunk(5, MissingReason.Vanished), new MissingChunk(6, MissingReason.Truncated)],
            r.Missing.OrderBy(m => m.Chunk));
        var m = TrickplayManifest.TryRead(Trickplay.DirFor(data, "k"))!;
        Assert.Equal([1, 2, 4, 7, 8], m.Sheets.Single().Chunks);
        Assert.Equal(3, m.Missing.Count);
        Assert.All(progress, p => Assert.Equal(8, p.Total));
        Assert.Equal(3, progress.Max(p => p.Skipped));
        Assert.Equal(5, progress.Max(p => p.Fed));

        var idx = TrickplayIndex.TryLoad(data, "k")!;
        Assert.Null(idx.Find(2 * 3.0 + 1));            // chunk 3: no thumbnail
        Assert.Equal(2, idx.FindNearest(2 * 3.0 + 1)!.Chunk);
    }

    [Fact]
    public async Task Rerun_only_processes_new_chunks_and_refills_the_open_sheet()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var folder = session.CopyTo(t.Sub("steam", "video", "bg_test"), 12);
        var data = NewData(t);

        var first = await Trickplay.GenerateAsync(data, "k", Media(folder, 1, 10), Cpu);
        Assert.Equal(10, first.NewChunks);
        var dir = Trickplay.DirFor(data, "k");
        var sheet = Path.Combine(dir, "sheet-0000.jpg");
        var stamp = File.GetLastWriteTimeUtc(sheet);
        var manifestBefore = File.ReadAllText(Path.Combine(dir, "manifest.json"));

        await Task.Delay(30);
        var again = await Trickplay.GenerateAsync(data, "k", Media(folder, 1, 10), Cpu);
        Assert.Equal((0, 0, 0), (again.NewChunks, again.Rebuilt, again.SheetsWritten));
        Assert.Equal(stamp, File.GetLastWriteTimeUtc(sheet));
        Assert.Equal(manifestBefore, File.ReadAllText(Path.Combine(dir, "manifest.json")));

        // the session grew by two chunks, and Steam trimmed the first two from the head
        File.Delete(Path.Combine(folder, FolderNames.ChunkFile(0, 1)));
        File.Delete(Path.Combine(folder, FolderNames.ChunkFile(0, 2)));
        var grown = await Trickplay.GenerateAsync(data, "k", Media(folder, 3, 12), Cpu);
        Assert.Equal(2, grown.NewChunks);
        Assert.Equal(8, grown.Rebuilt);
        Assert.Equal(1, grown.SheetsWritten);
        var m = TrickplayManifest.TryRead(dir)!;
        Assert.Equal(Enumerable.Range(3, 10), m.Sheets.Single().Chunks);
        Assert.Empty(m.Missing);
    }

    [Fact]
    public async Task Cancellation_commits_nothing_wrong_and_a_later_run_resumes()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var data = NewData(t);
        var media = Media(session.Folder, 1, SyntheticSession.Chunks);
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Trickplay.GenerateAsync(data, "k", media, Cpu, null, cts.Token));
        var dir = Trickplay.DirFor(data, "k");
        Assert.DoesNotContain(Directory.GetFileSystemEntries(dir), e => Path.GetFileName(e).StartsWith(".tmp-", StringComparison.Ordinal));

        var r = await Trickplay.GenerateAsync(data, "k", media, Cpu);
        Assert.Equal(SyntheticSession.Chunks, TrickplayManifest.TryRead(dir)!.Sheets.Sum(s => s.Chunks.Count));
        Assert.Equal(SyntheticSession.Chunks, r.NewChunks);
    }

    [Fact]
    public async Task Gpu_mode_always_ends_with_a_valid_result_falling_back_to_cpu_when_needed()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var data = NewData(t);
        var r = await Trickplay.GenerateAsync(data, "k", Media(session.Folder, 1, 6), new TrickplayOptions(DecodeMode.Gpu));
        output.WriteLine($"Gpu requested, used {r.UsedMode}");
        Assert.Equal(6, r.NewChunks);
        Assert.Equal(6, TrickplayManifest.TryRead(Trickplay.DirFor(data, "k"))!.Sheets.Single().Chunks.Count);
    }

    [Fact]
    public async Task Missing_init_segment_is_a_clear_error_and_writes_nothing()
    {
        if (!CanRun()) return;
        using var t = new TempDir();
        var data = NewData(t);
        var empty = Directory.CreateDirectory(t.Sub("steam", "video", "bg_x")).FullName;
        await Assert.ThrowsAsync<FileNotFoundException>(() => Trickplay.GenerateAsync(data, "k", Media(empty, 1, 3), Cpu));
    }

    private sealed class SyncProgress(Action<TrickplayProgress> sink) : IProgress<TrickplayProgress>
    {
        public void Report(TrickplayProgress value) { lock (sink) sink(value); }
    }
}
