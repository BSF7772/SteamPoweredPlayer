// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text;
using SteamPoweredPlayer.Core.Steam;
using SteamPoweredPlayer.Tests.Fakes;

namespace SteamPoweredPlayer.Tests.Library;

/// <summary>A synthetic Steam tree (recording root, Steam folder with one app manifest, data folder path) under the temp directory.</summary>
internal sealed class SynthLibrary : IDisposable
{
    public static readonly byte[] BoxHead = [0, 0, 0, 0x18, (byte)'s', (byte)'t', (byte)'y', (byte)'p'];

    private readonly TempDir _t = new();
    public ManualTimeProvider Clock { get; } = new();

    public SynthLibrary()
    {
        Directory.CreateDirectory(Root);
        Directory.CreateDirectory(Path.Combine(SteamRoot, "steamapps"));
        File.WriteAllText(Path.Combine(SteamRoot, "steamapps", "appmanifest_730.acf"), "\"AppState\"\n{\n\t\"name\"\t\"Test Game\"\n}\n");
    }

    public string Root => _t.Sub("Rec");
    public string DataPath => _t.Sub("data");
    public string SteamRoot => _t.Sub("Steam");
    public GameNameResolver Names => new(new SteamPaths(SteamRoot), null);
    public Core.Library.DataFolder Data => Core.Library.DataFolder.Open(DataPath, Root);

    /// <summary>Default last-write time of files: 10 minutes before the manual clock, i.e. not live.</summary>
    public DateTime Old => Clock.GetUtcNow().UtcDateTime.AddMinutes(-10);

    public string Bg(string name = "bg_730_20250101_120000") => Path.Combine(Root, "video", name);

    public string AddBg(string name, int first, int last, DateTime? t = null)
    {
        var dir = Bg(name);
        Fill(dir, first, last, t);
        return dir;
    }

    public string AddClip(string clip, string media, int first, int last)
    {
        var dir = Path.Combine(Root, "clips", clip);
        Fill(Path.Combine(dir, "video", media), first, last, null);
        return dir;
    }

    public void Fill(string mediaDir, int first, int last, DateTime? t)
    {
        Directory.CreateDirectory(mediaDir);
        Write(mediaDir, "init-stream0.m4s", t);
        Write(mediaDir, "init-stream1.m4s", t);
        for (var n = first; n <= last; n++) AddChunk(mediaDir, n, t);
    }

    public void AddChunk(string mediaDir, int n, DateTime? t = null)
    {
        Write(mediaDir, FolderNames.ChunkFile(0, n), t);
        Write(mediaDir, FolderNames.ChunkFile(1, n), t);
    }

    public void Write(string dir, string file, DateTime? t = null, byte[]? bytes = null)
    {
        var p = Path.Combine(dir, file);
        File.WriteAllBytes(p, bytes ?? [.. BoxHead, .. new byte[32]]);
        File.SetLastWriteTimeUtc(p, t ?? Old);
    }

    public void WriteMpd(string dir, string type, DateTime t) =>
        Write(dir, "session.mpd", t, Encoding.UTF8.GetBytes($"<?xml version=\"1.0\"?>\n<MPD type=\"{type}\">\n</MPD>"));

    /// <summary>Every file below <paramref name="dir"/>: relative path, length, last write ticks. To prove nothing was touched.</summary>
    public static List<string> Snapshot(string dir) =>
    [
        .. Directory.EnumerateFileSystemEntries(dir, "*", SearchOption.AllDirectories)
            .Select(p => new FileInfo(p) is { Exists: true } f ? $"{Path.GetRelativePath(dir, p)}|{f.Length}|{f.LastWriteTimeUtc.Ticks}" : Path.GetRelativePath(dir, p) + "|dir")
            .Order(StringComparer.Ordinal),
    ];

    public void Dispose() => _t.Dispose();
}
