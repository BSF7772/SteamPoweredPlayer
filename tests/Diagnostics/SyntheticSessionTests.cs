// SPDX-License-Identifier: GPL-3.0-or-later
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Diagnostics;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Diagnostics;

/// <summary>The Core synthetic generator: every variant scans the way the self-test expects. Skipped (not failed) without ffmpeg.</summary>
[Collection("ffmpeg")]
public sealed class SyntheticSessionTests : IAsyncLifetime
{
    private SteamPoweredPlayer.Core.Diagnostics.SyntheticSession? _s;
    private string? _skip;

    public async Task InitializeAsync()
    {
        Ff.Locate();
        if (!Ff.Available) { _skip = "ffmpeg not found"; return; }
        _s = await SteamPoweredPlayer.Core.Diagnostics.SyntheticSession.CreateAsync(24);
    }

    public Task DisposeAsync()
    {
        _s?.Dispose();
        return Task.CompletedTask;
    }

    private static string Runs(SessionMedia m) => string.Join(',', m.Runs.Select(r => $"{r.First}-{r.Last}"));

    [Fact]
    public async Task Variants_scan_as_the_cases_they_model()
    {
        if (_skip is not null) return;
        var s = _s!;
        async Task<ScanResult> Scan(SyntheticVariant v, int chunks = 24) => await Scanner.ScanAsync(s.Build(v, chunks));

        var normal = await Scan(SyntheticVariant.Normal);
        Assert.Equal("1-24", Runs(normal.Media!));
        Assert.False(normal.Media!.Live);
        Assert.True(normal.Media.Temporary);
        Assert.False(normal.Crashed);

        Assert.Equal("11-24", Runs((await Scan(SyntheticVariant.TrimmedHead)).Media!));
        Assert.Equal(ManifestKind.Static, (await Scan(SyntheticVariant.StaleManifest)).Manifest);

        var zero = await Scan(SyntheticVariant.ZeroManifest);
        Assert.Equal(ManifestKind.ZeroFilled, zero.Manifest);
        Assert.True(zero.Crashed);

        Assert.Equal("1-15,21-24", Runs((await Scan(SyntheticVariant.Gap)).Media!));

        var live = await Scan(SyntheticVariant.Live);
        Assert.True(live.Media!.Live);
        Assert.Equal("1-12", Runs(live.Media));

        var crashed = await Scan(SyntheticVariant.Crashed);
        Assert.True(crashed.Crashed);
        Assert.Contains(crashed.Issues, i => i.Kind == ScanIssueKind.StaleTemporaryChunk);

        var noAudio = await Scan(SyntheticVariant.MissingAudioChunk);
        Assert.Equal("1-7,9-20", Runs(noAudio.Media!));
        Assert.Contains(noAudio.Issues, i => i is { Kind: ScanIssueKind.MissingAudioChunk, Chunk: 8 });

        var zeroChunk = await Scan(SyntheticVariant.ZeroFilledChunk);
        Assert.Equal("1-8,10-20", Runs(zeroChunk.Media!));
        Assert.Contains(zeroChunk.Issues, i => i is { Kind: ScanIssueKind.ZeroFilledChunk, Chunk: 9 });
    }

    [Fact]
    public async Task Chunks_use_steams_names_and_the_dash_layout()
    {
        if (_skip is not null) return;
        var s = _s!;
        Assert.True(File.Exists(Path.Combine(s.BaseFolder, FolderNames.InitFile(0))));
        Assert.True(File.Exists(Path.Combine(s.BaseFolder, FolderNames.InitFile(1))));
        for (var n = 1; n <= s.Chunks; n++)
        {
            Assert.True(File.Exists(Path.Combine(s.BaseFolder, FolderNames.ChunkFile(0, n))));
            Assert.True(File.Exists(Path.Combine(s.BaseFolder, FolderNames.ChunkFile(1, n))));
        }
        var folder = s.Build(SyntheticVariant.Normal, 24);
        Assert.NotNull(FolderNames.ParseRecording(Path.GetFileName(folder)));
        await Task.CompletedTask;
    }

    [Fact]
    public async Task Live_timer_adds_chunks_the_way_steam_does_and_head_trim_removes_them()
    {
        if (_skip is not null) return;
        var s = _s!;
        var folder = s.Build(SyntheticVariant.Live);
        using (var live = s.StartLive(folder, 13, TimeSpan.FromMilliseconds(50)))
        {
            await WaitFor(() => live.LastNumber >= 15);
        }
        var grown = (await Scanner.ScanAsync(folder)).Media!;
        Assert.True(grown.LastChunk >= 15);
        Assert.DoesNotContain(Directory.GetFiles(folder), f => f.EndsWith(".tmp", StringComparison.Ordinal));

        using (var trim = s.StartHeadTrim(folder, 1, 3, TimeSpan.FromMilliseconds(30)))
        {
            await WaitFor(() => trim.LastNumber >= 3);
        }
        Assert.Equal(4, (await Scanner.ScanAsync(folder)).Media!.FirstChunk);
    }

    [Fact]
    public async Task Dispose_removes_only_its_own_directory()
    {
        if (_skip is not null) return;
        var other = SteamPoweredPlayer.Core.Diagnostics.SyntheticSession.CreateAsync(3);
        var s2 = await other;
        var root = s2.Root;
        Assert.True(Directory.Exists(root));
        s2.Dispose();
        Assert.False(Directory.Exists(root));
        Assert.True(Directory.Exists(_s!.Root));
    }

    private static async Task WaitFor(Func<bool> cond)
    {
        var until = DateTime.UtcNow.AddSeconds(10);
        while (!cond() && DateTime.UtcNow < until) await Task.Delay(20);
        Assert.True(cond(), "timed out");
    }
}
