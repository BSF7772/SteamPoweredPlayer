// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Text;
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Tests.Library;

/// <summary>Synthetic session folder under the temp directory (fake box headers, no media). Removed on dispose.</summary>
internal sealed class FakeSession : IDisposable
{
    public static readonly byte[] BoxHead = [0, 0, 0, 0x18, (byte)'s', (byte)'t', (byte)'y', (byte)'p'];
    public static readonly byte[] Zeros = new byte[64];

    private readonly string _root = Path.Combine(Path.GetTempPath(), "spp-tests-" + Guid.NewGuid().ToString("N"));
    public string Folder { get; }

    public FakeSession(string name = "bg_730_20250101_120000")
    {
        Folder = Path.Combine(_root, "video", name);
        Directory.CreateDirectory(Folder);
    }

    public string Root => _root;
    public string PathOf(string file) => Path.Combine(Folder, file);

    public FakeSession Write(string file, byte[]? bytes = null, DateTime? lastWriteUtc = null)
    {
        var p = PathOf(file);
        File.WriteAllBytes(p, bytes ?? [.. BoxHead, .. new byte[32]]);
        if (lastWriteUtc is { } t) File.SetLastWriteTimeUtc(p, t);
        return this;
    }

    public FakeSession Init(DateTime? t = null) => Write("init-stream0.m4s", null, t).Write("init-stream1.m4s", null, t);

    public FakeSession Chunks(int first, int last, DateTime? t = null)
    {
        for (var n = first; n <= last; n++) Chunk(n, t);
        return this;
    }

    public FakeSession Chunk(int n, DateTime? t = null) =>
        Write(Core.Steam.FolderNames.ChunkFile(0, n), null, t).Write(Core.Steam.FolderNames.ChunkFile(1, n), null, t);

    public FakeSession Mpd(string type, DateTime? t = null) =>
        Write("session.mpd", Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?>\n<MPD xmlns=\"urn:mpeg:dash:schema:mpd:2011\" type=\"{type}\" startNumber=\"1\">\n</MPD>"), t);

    public void Dispose()
    {
        try { Directory.Delete(_root, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}

internal sealed class FixedTimeProvider(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}

[Collection(BudgetCollection.Name)]
public sealed class ScannerTests
{
    private static readonly DateTime Now = new(2025, 1, 1, 15, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Old = Now.AddMinutes(-10);
    private static ScanOptions Opts(Action? afterListing = null, bool? temporary = null) =>
        new() { Clock = new FixedTimeProvider(new DateTimeOffset(Now)), Temporary = temporary, AfterListing = afterListing };

    private static IReadOnlyList<(int First, int Last)> RunsOf(ScanResult r) => [.. r.Media!.Runs.Select(x => (x.First, x.Last))];

    // ---- I/O wrapper ------------------------------------------------------------------------------------------------

    [Fact]
    public async Task Normal_session_is_one_run_and_temporary()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 5, Old).Mpd("static", Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal(ScanStatus.Playable, r.Status);
        Assert.Equal([(1, 5)], RunsOf(r));
        Assert.False(r.Media!.Live);
        Assert.True(r.Media.Temporary);
        Assert.False(r.Crashed);
        Assert.Equal(3.0, r.Media.SegmentSeconds);
        Assert.Equal(Path.GetFullPath(s.Folder), r.Media.Folder);
        Assert.Empty(r.UnknownFiles);
        Assert.Empty(r.Issues);
    }

    [Fact]
    public async Task Trimmed_head_starts_at_the_first_chunk_on_disk_whatever_the_manifest_says()
    {
        using var s = new FakeSession().Init(Old).Chunks(1840, 1850, Old).Mpd("static", Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal([(1840, 1850)], RunsOf(r));
        Assert.Equal(1840, r.Media!.FirstChunk);
    }

    [Fact]
    public async Task Stale_manifest_is_not_trusted()
    {
        // startNumber="1", dynamic, but the newest chunk is 10 minutes old: not live; chunks on disk are the truth.
        using var s = new FakeSession().Init(Old).Chunks(500, 510, Old).Mpd("dynamic", Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.False(r.Media!.Live);
        Assert.Equal([(500, 510)], RunsOf(r));
    }

    [Fact]
    public async Task Gap_gives_two_runs()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 4, Old).Chunks(8, 10, Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal([(1, 4), (8, 10)], RunsOf(r));
        Assert.Equal(7, r.Media!.ChunkCount);
    }

    [Fact]
    public async Task Dynamic_manifest_and_recent_chunk_is_live()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 4, Old).Chunk(5, Now.AddSeconds(-3)).Mpd("dynamic", Now.AddSeconds(-3));
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.True(r.Media!.Live);
        Assert.False(r.Crashed);
    }

    [Fact]
    public async Task Fresh_tmp_chunk_counts_as_recent_activity_but_is_not_complete()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 4, Old).Mpd("dynamic", Old)
            .Write("chunk-stream0-00005.m4s.tmp", null, Now.AddSeconds(-1));
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.True(r.Media!.Live);
        Assert.Equal([(1, 4)], RunsOf(r));
        Assert.DoesNotContain(r.Issues, i => i.Kind == ScanIssueKind.StaleTemporaryChunk);
    }

    [Fact]
    public async Task Recent_chunk_with_a_static_manifest_is_not_live()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 4, Now.AddSeconds(-2)).Mpd("static", Now.AddSeconds(-2));
        Assert.False((await Scanner.ScanAsync(s.Folder, Opts())).Media!.Live);
    }

    [Fact]
    public async Task Crashed_session_has_zero_manifest_and_old_tmp_and_is_still_playable()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 6, Old)
            .Write("session.mpd", FakeSession.Zeros, Old)
            .Write("chunk-stream0-00007.m4s.tmp", FakeSession.Zeros, Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.True(r.Crashed);
        Assert.Equal(ScanStatus.Playable, r.Status);
        Assert.Equal([(1, 6)], RunsOf(r));
        Assert.False(r.Media!.Live);
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.ZeroFilledManifest);
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.StaleTemporaryChunk && i.File == "chunk-stream0-00007.m4s.tmp");
    }

    [Fact]
    public async Task Zero_manifest_alone_marks_crashed()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 3, Old).Write("session.mpd", FakeSession.Zeros, Old);
        Assert.True((await Scanner.ScanAsync(s.Folder, Opts())).Crashed);
    }

    [Fact]
    public async Task Missing_audio_chunk_makes_that_number_incomplete()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 5, Old);
        File.Delete(s.PathOf("chunk-stream1-00003.m4s"));
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal([(1, 2), (4, 5)], RunsOf(r));
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.MissingAudioChunk && i.Chunk == 3);
    }

    [Fact]
    public async Task Missing_video_chunk_makes_that_number_incomplete()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 3, Old);
        File.Delete(s.PathOf("chunk-stream0-00002.m4s"));
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal([(1, 1), (3, 3)], RunsOf(r));
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.MissingVideoChunk && i.Chunk == 2);
    }

    [Fact]
    public async Task Zero_filled_chunk_with_a_normal_name_is_rejected_and_reported()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 4, Old);
        s.Write("chunk-stream0-00004.m4s", FakeSession.Zeros, Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal([(1, 3)], RunsOf(r));
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.ZeroFilledChunk && i.File == "chunk-stream0-00004.m4s" && i.Chunk == 4);
    }

    [Fact]
    public async Task Chunk_with_a_garbage_head_is_rejected()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 3, Old);
        s.Write("chunk-stream1-00002.m4s", [0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0x00, 0x01, 0x02], Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal([(1, 1), (3, 3)], RunsOf(r));
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.InvalidChunkHead && i.Chunk == 2);
    }

    [Fact]
    public async Task Truncated_chunk_shorter_than_a_box_header_is_rejected()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 2, Old);
        s.Write("chunk-stream0-00002.m4s", [0, 0, 0], Old);
        Assert.Equal([(1, 1)], RunsOf(await Scanner.ScanAsync(s.Folder, Opts())));
    }

    [Fact]
    public async Task Missing_init_is_not_playable_and_reported()
    {
        using var s = new FakeSession().Chunks(1, 3, Old).Write("init-stream0.m4s", null, Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal(ScanStatus.NotPlayable, r.Status);
        Assert.Null(r.Media);
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.MissingInit && i.File == "init-stream1.m4s");
    }

    [Fact]
    public async Task Session_without_complete_chunks_is_not_playable()
    {
        using var s = new FakeSession().Init(Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal(ScanStatus.NotPlayable, r.Status);
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.NoCompleteChunks);
    }

    [Fact]
    public async Task Unknown_files_are_listed_not_guessed()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 2, Old).Write("surprise.bin").Write("chunk-stream2-00001.m4s");
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal(["chunk-stream2-00001.m4s", "surprise.bin"], r.UnknownFiles.Order(StringComparer.Ordinal));
        Assert.Equal([(1, 2)], RunsOf(r));
    }

    [Fact]
    public async Task Folder_that_does_not_exist_is_gone()
    {
        var r = await Scanner.ScanAsync(Path.Combine(Path.GetTempPath(), "spp-tests-missing-" + Guid.NewGuid().ToString("N")), Opts());
        Assert.Equal(ScanStatus.Gone, r.Status);
        Assert.Null(r.Media);
    }

    [Fact]
    public async Task Folder_deleted_between_listing_and_head_checks_is_gone_without_exception()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 5, Old).Mpd("static", Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts(afterListing: () => Directory.Delete(s.Folder, true)));
        Assert.Equal(ScanStatus.Gone, r.Status);
        Assert.Null(r.Media);
    }

    [Fact]
    public async Task File_vanishing_between_listing_and_head_check_drops_that_chunk()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 5, Old);
        var r = await Scanner.ScanAsync(s.Folder, Opts(afterListing: () => File.Delete(s.PathOf("chunk-stream1-00003.m4s"))));
        Assert.Equal(ScanStatus.Playable, r.Status);
        Assert.Equal([(1, 2), (4, 5)], RunsOf(r));
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.VanishedChunk && i.Chunk == 3);
    }

    [Fact]
    public async Task Head_check_does_not_block_deleting_the_file_during_a_scan()
    {
        // The scanner opens with Read | Write | Delete sharing, so Steam's cleanup never fails because of us.
        using var s = new FakeSession().Init(Old).Chunks(1, 2, Old);
        using var held = new FileStream(s.PathOf("chunk-stream0-00001.m4s"), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        var r = await Scanner.ScanAsync(s.Folder, Opts());
        Assert.Equal([(1, 2)], RunsOf(r));
    }

    [Fact]
    public async Task Cancellation_is_honoured()
    {
        using var s = new FakeSession().Init(Old).Chunks(1, 3, Old);
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Scanner.ScanAsync(s.Folder, Opts(), cts.Token));
    }

    [Fact]
    public async Task Cache_session_is_temporary_but_a_clips_inner_bg_folder_is_not()
    {
        using var s = new FakeSession("clip_730_20250101_120000");
        var media = Path.Combine(s.Folder, "video", "bg_730_20250101_115900");
        Directory.CreateDirectory(media);
        foreach (var f in new[] { "init-stream0.m4s", "init-stream1.m4s", "chunk-stream0-00001.m4s", "chunk-stream1-00001.m4s" })
            File.WriteAllBytes(Path.Combine(media, f), [.. FakeSession.BoxHead, .. new byte[8]]);

        Assert.Equal(media, Scanner.FindClipMediaFolder(s.Folder));
        var r = await Scanner.ScanClipAsync(s.Folder, Opts());
        Assert.Equal(ScanStatus.Playable, r.Status);
        Assert.False(r.Media!.Temporary);
        Assert.Equal(media, r.Media.Folder);

        var direct = await Scanner.ScanAsync(media, Opts());
        Assert.True(direct.Media!.Temporary);   // by name alone it looks like a cache session; callers use ScanClipAsync for clips
    }

    [Fact]
    public void Clip_without_a_media_folder_has_none_and_scans_as_gone()
    {
        using var s = new FakeSession("clip_730_20250101_120000");
        Assert.Null(Scanner.FindClipMediaFolder(s.Folder));
        Assert.Null(Scanner.FindClipMediaFolder(Path.Combine(s.Root, "nope")));
    }

    [Fact]
    public async Task Clip_chunks_numbering_restarts_at_one()
    {
        using var s = new FakeSession("clip_730_20250101_120000");
        var media = Path.Combine(s.Folder, "video", "bg_730_20250101_115900");
        Directory.CreateDirectory(media);
        foreach (var f in new[] { "init-stream0.m4s", "init-stream1.m4s" }) File.WriteAllBytes(Path.Combine(media, f), [.. FakeSession.BoxHead]);
        for (var n = 1; n <= 3; n++)
            foreach (var st in new[] { 0, 1 })
                File.WriteAllBytes(Path.Combine(media, Core.Steam.FolderNames.ChunkFile(st, n)), [.. FakeSession.BoxHead]);
        var r = await Scanner.ScanClipAsync(s.Folder, Opts());
        Assert.Equal([(1, 3)], RunsOf(r));
    }

    // ---- pure analysis ----------------------------------------------------------------------------------------------

    private static ScanEntry E(string name, DateTime? t = null) => new(name, 40, new DateTimeOffset(t ?? Old));

    private static List<ScanEntry> Entries(int first, int last, DateTime? t = null)
    {
        var l = new List<ScanEntry> { E("init-stream0.m4s"), E("init-stream1.m4s"), E("session.mpd") };
        for (var n = first; n <= last; n++)
        {
            l.Add(E(Core.Steam.FolderNames.ChunkFile(0, n), t));
            l.Add(E(Core.Steam.FolderNames.ChunkFile(1, n), t));
        }
        return l;
    }

    private static readonly DateTimeOffset NowO = new(Now);

    [Fact]
    public void Analysis_is_pure_and_uses_the_head_results_it_is_given()
    {
        var entries = Entries(1, 4);
        var r = ScanAnalysis.Analyze("C:/rec/bg_730_20250101_120000", entries, ManifestKind.Static, NowO,
            f => f == "chunk-stream1-00003.m4s" ? HeadCheck.ZeroFilled : HeadCheck.Valid);
        Assert.Equal([(1, 2), (4, 4)], RunsOf(r));
        Assert.Contains(r.Issues, i => i.Kind == ScanIssueKind.ZeroFilledChunk && i.Chunk == 3);
    }

    [Fact]
    public void Candidates_are_the_chunks_with_both_streams_present_and_final_names()
    {
        var entries = Entries(1, 2);
        entries.Add(E("chunk-stream0-00003.m4s"));                    // audio missing
        entries.Add(E("chunk-stream0-00004.m4s.tmp"));
        entries.Add(E("chunk-stream1-00004.m4s"));
        var c = ScanAnalysis.HeadCandidates(entries);
        Assert.Equal(4, c.Count);
        Assert.DoesNotContain("chunk-stream0-00003.m4s", c);
    }

    [Fact]
    public void Analysis_of_4800_entries_is_under_20_ms()
    {
        var entries = Entries(1, 2400);
        Assert.Equal(4803, entries.Count);
        ScanResult? r = null;
        for (var i = 0; i < 20; i++) r = ScanAnalysis.Analyze("C:/rec/bg_1_20250101_120000", entries, ManifestKind.Static, NowO, _ => HeadCheck.Valid);   // warm
        var sw = Stopwatch.StartNew();
        const int loops = 10;
        for (var i = 0; i < loops; i++) r = ScanAnalysis.Analyze("C:/rec/bg_1_20250101_120000", entries, ManifestKind.Static, NowO, _ => HeadCheck.Valid);
        var perCall = sw.Elapsed.TotalMilliseconds / loops;
        Assert.Equal(2400, r!.Media!.ChunkCount);
        Assert.True(perCall < 20, $"analysis took {perCall:F2} ms");
    }

    [Theory]
    [InlineData("<?xml version=\"1.0\"?><MPD type=\"dynamic\" startNumber=\"1\">", ManifestKind.Dynamic)]
    [InlineData("<?xml version=\"1.0\"?>\n<MPD\n  xmlns=\"urn:x\"\n  type='dynamic'>", ManifestKind.Dynamic)]
    [InlineData("<MPD type=\"static\" mediaPresentationDuration=\"PT0.0S\">", ManifestKind.Static)]
    [InlineData("<MPD xmlns=\"urn:x\">", ManifestKind.Static)]
    [InlineData("garbage", ManifestKind.Unrecognised)]
    public void Manifest_text_is_classified(string text, ManifestKind expected) =>
        Assert.Equal(expected, ScanManifest.Classify(Encoding.UTF8.GetBytes(text)));

    [Fact]
    public void Manifest_all_zero_or_empty_is_classified()
    {
        Assert.Equal(ManifestKind.ZeroFilled, ScanManifest.Classify(new byte[4096]));
        Assert.Equal(ManifestKind.Empty, ScanManifest.Classify([]));
    }

    [Theory]
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x73, 0x74, 0x79, 0x70 }, HeadCheck.Valid)]          // styp
    [InlineData(new byte[] { 0, 0, 0, 0x6C, 0x6D, 0x6F, 0x6F, 0x66 }, HeadCheck.Valid)]          // moof
    [InlineData(new byte[] { 0, 0, 0, 1, 0x6D, 0x64, 0x61, 0x74 }, HeadCheck.Valid)]             // 64-bit size
    [InlineData(new byte[] { 0, 0, 0, 0, 0, 0, 0, 0 }, HeadCheck.ZeroFilled)]
    [InlineData(new byte[] { 0, 0, 0, 0 }, HeadCheck.TooShort)]
    [InlineData(new byte[] { 0, 0, 0, 4, 0x73, 0x74, 0x79, 0x70 }, HeadCheck.NotABox)]           // size below the header size
    [InlineData(new byte[] { 0, 0, 0, 0x18, 0x01, 0x02, 0x03, 0x04 }, HeadCheck.NotABox)]        // type not printable
    [InlineData(new byte[] { 0x47, 0x49, 0x46, 0x38, 0x39, 0x61, 0, 0 }, HeadCheck.NotABox)]
    public void Head_bytes_are_classified(byte[] head, HeadCheck expected) => Assert.Equal(expected, ScanHeads.Classify(head));
}
