// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Tests.Library;

public sealed class ArtworkTests
{
    private static (TempDir t, DataFolder f, ArtworkStore store, string src) Setup()
    {
        var t = new TempDir();
        var src = t.Sub("steam-cache");
        Directory.CreateDirectory(src);
        return (t, DataFolder.Open(t.Sub("data"), t.Sub("Rec")), new ArtworkStore(DataFolder.Open(t.Sub("data"), t.Sub("Rec"))), src);
    }

    private static string Make(string dir, string name, int size, DateTime? time = null)
    {
        var p = Path.Combine(dir, name);
        File.WriteAllBytes(p, Enumerable.Range(0, size).Select(i => (byte)(i * 7)).ToArray());
        File.SetLastWriteTimeUtc(p, time ?? new DateTime(2024, 5, 5, 5, 5, 5, DateTimeKind.Utc));
        return p;
    }

    [Fact]
    public async Task Copies_each_role_to_art_gameId_and_returns_relative_forward_slash_paths()
    {
        var (t, f, store, src) = Setup();
        using var _ = t;
        var art = await store.CopyAsync("730", new ArtworkSource(Make(src, "a.JPG", 100), Make(src, "b.jpg", 200), Make(src, "c.jpg", 300), Make(src, "d.png", 400)));
        Assert.Equal(new GameArt("art/730/cover.jpg", "art/730/hero.jpg", "art/730/heroBlur.jpg", "art/730/logo.png"), art);
        Assert.Equal(100, new FileInfo(Path.Combine(f.ArtDir, "730", "cover.jpg")).Length);
        Assert.Equal(400, new FileInfo(Path.Combine(f.ArtDir, "730", "logo.png")).Length);
    }

    [Fact]
    public async Task Missing_or_null_sources_give_null_entries()
    {
        var (t, _, store, src) = Setup();
        using var _ = t;
        var art = await store.CopyAsync("730", new ArtworkSource(Make(src, "a.jpg", 10), null, Path.Combine(src, "nope.jpg")));
        Assert.Equal("art/730/cover.jpg", art.Cover);
        Assert.Null(art.Hero);
        Assert.Null(art.HeroBlur);
        Assert.Null(art.Logo);
    }

    [Fact]
    public async Task Same_size_and_mtime_is_not_copied_again()
    {
        var (t, f, store, src) = Setup();
        using var _ = t;
        var s = Make(src, "a.jpg", 100);
        await store.CopyAsync("730", new ArtworkSource(s));
        var dest = Path.Combine(f.ArtDir, "730", "cover.jpg");

        // Tamper with the copy but keep size + mtime: a skipped copy leaves the tampering in place.
        var mtime = File.GetLastWriteTimeUtc(dest);
        File.WriteAllBytes(dest, new byte[100]);
        File.SetLastWriteTimeUtc(dest, mtime);
        await store.CopyAsync("730", new ArtworkSource(s));
        Assert.All(File.ReadAllBytes(dest), b => Assert.Equal(0, b));
    }

    [Fact]
    public async Task Changed_source_is_copied_again()
    {
        var (t, f, store, src) = Setup();
        using var _ = t;
        var s = Make(src, "a.jpg", 100);
        await store.CopyAsync("730", new ArtworkSource(s));
        Make(src, "a.jpg", 150, new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await store.CopyAsync("730", new ArtworkSource(s));
        Assert.Equal(150, new FileInfo(Path.Combine(f.ArtDir, "730", "cover.jpg")).Length);
    }

    [Fact]
    public async Task Same_size_but_newer_source_is_copied_again()
    {
        var (t, f, store, src) = Setup();
        using var _ = t;
        var s = Make(src, "a.jpg", 100);
        await store.CopyAsync("730", new ArtworkSource(s));
        File.SetLastWriteTimeUtc(s, new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc));
        await store.CopyAsync("730", new ArtworkSource(s));
        Assert.Equal(new DateTime(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc), File.GetLastWriteTimeUtc(Path.Combine(f.ArtDir, "730", "cover.jpg")));
    }

    [Fact]
    public async Task Source_is_never_modified_and_no_temp_files_remain()
    {
        var (t, f, store, src) = Setup();
        using var _ = t;
        var s = Make(src, "a.jpg", 100);
        var before = (File.ReadAllBytes(s), File.GetLastWriteTimeUtc(s));
        await store.CopyAsync("730", new ArtworkSource(s));
        Assert.Equal(before.Item1, File.ReadAllBytes(s));
        Assert.Equal(before.Item2, File.GetLastWriteTimeUtc(s));
        Assert.Empty(Directory.GetFiles(Path.Combine(f.ArtDir, "730"), "*.tmp"));
        Assert.Single(Directory.GetFiles(src));
    }

    [Fact]
    public async Task Source_held_open_by_steam_can_still_be_copied()
    {
        var (t, _, store, src) = Setup();
        using var _ = t;
        var s = Make(src, "a.jpg", 100);
        using var steam = new FileStream(s, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
        Assert.Equal("art/730/cover.jpg", (await store.CopyAsync("730", new ArtworkSource(s))).Cover);
    }

    [Fact]
    public async Task Unsafe_game_id_and_odd_extensions_are_contained()
    {
        var (t, f, store, src) = Setup();
        using var _ = t;
        await Assert.ThrowsAsync<ArgumentException>(() => store.CopyAsync("..", new ArtworkSource(Make(src, "a.jpg", 5))));
        var art = await store.CopyAsync("5", new ArtworkSource(Make(src, "noext", 5), Make(src, "x.a b", 5)));
        Assert.Equal("art/5/cover.img", art.Cover);
        Assert.Equal("art/5/hero.img", art.Hero);
        Assert.StartsWith(f.ArtDir, Path.GetFullPath(Path.Combine(f.Root, art.Cover!)));
    }

    [Fact]
    public async Task Cancellation_leaves_no_partial_copy()
    {
        var (t, f, store, src) = Setup();
        using var _ = t;
        using var cts = new CancellationTokenSource();
        cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => store.CopyAsync("730", new ArtworkSource(Make(src, "a.jpg", 5)), cts.Token));
        Assert.False(Directory.Exists(Path.Combine(f.ArtDir, "730")));
    }
}
