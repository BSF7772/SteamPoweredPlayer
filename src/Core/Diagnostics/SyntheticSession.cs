// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>The recording shapes of plan §3 that <see cref="SyntheticSession.Build"/> can lay out on disk.</summary>
public enum SyntheticVariant
{
    /// <summary>One run, chunks 1..N, a static manifest.</summary>
    Normal,
    /// <summary>Steam trimmed the head: chunks 11..N, the manifest still describes the whole session.</summary>
    TrimmedHead,
    /// <summary>All chunks, but the manifest is a static one that lists only a few of them.</summary>
    StaleManifest,
    /// <summary>All chunks, <c>session.mpd</c> is all zero bytes (a crash).</summary>
    ZeroManifest,
    /// <summary>Two runs: 1..15 and 21..N.</summary>
    Gap,
    /// <summary>12 chunks, a dynamic manifest and fresh timestamps; grow it with <see cref="SyntheticSession.StartLive"/>.</summary>
    Live,
    /// <summary>A zero-filled manifest and a stale <c>.tmp</c> pair after the last chunk.</summary>
    Crashed,
    /// <summary>Chunks 1..20 without the audio chunk 8.</summary>
    MissingAudioChunk,
    /// <summary>Chunks 1..20 where the video chunk 9 is all zero bytes under its normal name.</summary>
    ZeroFilledChunk,
}

/// <summary>
/// A synthetic Steam-style DASH session: ffmpeg's dash muxer with Steam's file names (<c>init-stream{0,1}.m4s</c>,
/// <c>chunk-stream{0,1}-NNNNN.m4s</c>), 3 s segments, 30 fps H.264 (one IDR per segment) and AAC audio. The content is generated once
/// (<see cref="CreateAsync"/>); <see cref="Build"/> copies parts of it into recording folders shaped like the real cases. Everything lives
/// under one temp directory that <see cref="Dispose"/> removes (and nothing else). Used by <c>--selftest</c>, <c>--bench</c> helpers and tests.
/// </summary>
public sealed class SyntheticSession : IDisposable
{
    public const int DefaultChunks = 58;
    public const int FramesPerSecond = 30;
    public const double SegmentSeconds = SessionMedia.DefaultSegmentSeconds;
    public const double FrameSeconds = 1.0 / FramesPerSecond;

    private int _folderCounter;

    private SyntheticSession(string root, int chunks)
    {
        Root = root;
        Chunks = chunks;
    }

    /// <summary>The temp directory that holds everything of this instance.</summary>
    public string Root { get; }

    /// <summary>Chunks of the generated base content (1..Chunks).</summary>
    public int Chunks { get; }

    public string BaseFolder => Path.Combine(Root, "base");

    /// <summary>A BackgroundRecordPath look-alike: variants go to <c>&lt;RecordingRoot&gt;/video/bg_730_*</c>.</summary>
    public string RecordingRoot => Path.Combine(Root, "rec");

    /// <summary>A directory next to the recordings for data folders and scratch files.</summary>
    public string Scratch(string name)
    {
        var p = Path.Combine(Root, "scratch", name);
        Directory.CreateDirectory(p);
        return p;
    }

    /// <summary>Last write time of files that must look old (not live, <c>.tmp</c> stale): ten minutes ago.</summary>
    public static DateTime Old => DateTime.UtcNow.AddMinutes(-10);

    /// <summary>Generates the base content with ffmpeg (needs <c>ffmpeg</c>, see <see cref="Ff.Locate"/>).</summary>
    /// <exception cref="InvalidOperationException">ffmpeg is missing or failed.</exception>
    public static async Task<SyntheticSession> CreateAsync(int chunks = DefaultChunks, string? parent = null, CancellationToken ct = default)
    {
        if (chunks < 3) throw new ArgumentOutOfRangeException(nameof(chunks));
        var root = Path.Combine(parent ?? Path.GetTempPath(), "spp-synth-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(root);
        var s = new SyntheticSession(root, chunks);
        try
        {
            await s.GenerateAsync(ct).ConfigureAwait(false);
            return s;
        }
        catch
        {
            s.Dispose();
            throw;
        }
    }

    private async Task GenerateAsync(CancellationToken ct)
    {
        if (!Ff.Available) Ff.Locate();
        if (!Ff.Available) throw new InvalidOperationException("ffmpeg was not found.");
        Directory.CreateDirectory(BaseFolder);
        var inv = CultureInfo.InvariantCulture;
        var psi = Ff.StartInfo(Ff.Ffmpeg,
        [
            "-hide_banner", "-loglevel", "error", "-y",
            "-f", "lavfi", "-i", $"testsrc2=size=320x180:rate={FramesPerSecond.ToString(inv)}",
            "-f", "lavfi", "-i", "sine=frequency=440:sample_rate=48000",
            "-t", (Chunks * SegmentSeconds + 1).ToString("0.###", inv),
            "-c:v", "libx264", "-preset", "ultrafast", "-g", (FramesPerSecond * 3).ToString(inv), "-keyint_min", (FramesPerSecond * 3).ToString(inv),
            "-sc_threshold", "0", "-pix_fmt", "yuv420p",
            "-c:a", "aac", "-b:a", "64k",
            "-f", "dash", "-seg_duration", "3", "-use_template", "1", "-use_timeline", "0",
            "-init_seg_name", "init-stream$RepresentationID$.m4s",
            "-media_seg_name", "chunk-stream$RepresentationID$-$Number%05d$.m4s",
            "-adaptation_sets", "id=0,streams=v id=1,streams=a",
            "session.mpd",
        ]);
        psi.WorkingDirectory = BaseFolder;
        using var p = Process.Start(psi) ?? throw new InvalidOperationException("ffmpeg did not start.");
        var err = p.StandardError.ReadToEndAsync(ct);
        _ = p.StandardOutput.ReadToEndAsync(ct);
        await p.WaitForExitAsync(ct).ConfigureAwait(false);
        if (p.ExitCode != 0) throw new InvalidOperationException("ffmpeg failed to generate the synthetic session: " + Redactor.Scrub(await err.ConfigureAwait(false)));
        for (var n = 1; n <= Chunks; n++)
            foreach (var stream in new[] { 0, 1 })
                if (!File.Exists(Path.Combine(BaseFolder, FolderNames.ChunkFile(stream, n))))
                    throw new InvalidOperationException($"The synthetic session lacks chunk {n} of stream {stream}.");
    }

    // ---- layouts --------------------------------------------------------------------------------------------------

    /// <summary>A new, empty recording folder named like a cache session (<c>bg_730_20260101_1200NN</c>).</summary>
    public string NewRecordingFolder()
    {
        var n = Interlocked.Increment(ref _folderCounter);
        var dir = Path.Combine(RecordingRoot, "video", string.Create(CultureInfo.InvariantCulture, $"bg_730_20260101_12{n:D4}"));
        Directory.CreateDirectory(dir);
        return dir;
    }

    /// <summary>Lays out <paramref name="variant"/> in a new recording folder and returns it.</summary>
    /// <param name="chunks">Chunks of the session for the variants that take a size (default: 40, at most <see cref="Chunks"/>).</param>
    public string Build(SyntheticVariant variant, int chunks = 40)
    {
        chunks = Math.Clamp(chunks, 24, Chunks);
        var dir = NewRecordingFolder();
        var old = Old;
        switch (variant)
        {
            case SyntheticVariant.Normal:
                CopyChunks(dir, 1, chunks, old);
                CopyManifest(dir, old);
                break;
            case SyntheticVariant.TrimmedHead:
                CopyChunks(dir, 11, chunks, old);
                CopyManifest(dir, old);       // still describes chunks 1..N: stale
                break;
            case SyntheticVariant.StaleManifest:
                CopyChunks(dir, 1, chunks, old);
                WriteText(dir, "session.mpd", old, "<?xml version=\"1.0\"?>\n<MPD type=\"static\" mediaPresentationDuration=\"PT9S\">\n</MPD>\n");
                break;
            case SyntheticVariant.ZeroManifest:
                CopyChunks(dir, 1, chunks, old);
                ZeroManifest(dir, old);
                break;
            case SyntheticVariant.Gap:
                CopyChunks(dir, 1, 15, old);
                CopyChunks(dir, 21, chunks, old, withInit: false);
                CopyManifest(dir, old);
                break;
            case SyntheticVariant.Live:
                var now = DateTime.UtcNow;
                CopyChunks(dir, 1, 12, now);
                WriteText(dir, "session.mpd", now, "<?xml version=\"1.0\"?>\n<MPD type=\"dynamic\">\n</MPD>\n");
                break;
            case SyntheticVariant.Crashed:
                CopyChunks(dir, 1, chunks, old);
                ZeroManifest(dir, old);
                foreach (var stream in new[] { 0, 1 })
                {
                    var tmp = Path.Combine(dir, FolderNames.ChunkFile(stream, chunks + 1) + ".tmp");
                    File.WriteAllBytes(tmp, new byte[512]);
                    File.SetLastWriteTimeUtc(tmp, old);
                }
                break;
            case SyntheticVariant.MissingAudioChunk:
                CopyChunks(dir, 1, 20, old);
                CopyManifest(dir, old);
                File.Delete(Path.Combine(dir, FolderNames.ChunkFile(1, 8)));
                break;
            case SyntheticVariant.ZeroFilledChunk:
                CopyChunks(dir, 1, 20, old);
                CopyManifest(dir, old);
                ZeroFill(dir, 0, 9);
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(variant));
        }
        return dir;
    }

    /// <summary>Copies the init segments (unless <paramref name="withInit"/> is false) and chunks <paramref name="first"/>..<paramref name="last"/> of both streams.</summary>
    public void CopyChunks(string dest, int first, int last, DateTime writeTimeUtc, bool withInit = true)
    {
        Directory.CreateDirectory(dest);
        if (withInit)
            foreach (var stream in new[] { 0, 1 })
                CopyOne(FolderNames.InitFile(stream), dest, writeTimeUtc);
        for (var n = first; n <= last; n++)
            foreach (var stream in new[] { 0, 1 })
                CopyOne(FolderNames.ChunkFile(stream, n), dest, writeTimeUtc);
    }

    private void CopyOne(string name, string dest, DateTime writeTimeUtc)
    {
        var to = Path.Combine(dest, name);
        File.Copy(Path.Combine(BaseFolder, name), to, overwrite: true);
        File.SetLastWriteTimeUtc(to, writeTimeUtc);
    }

    /// <summary>Adds one chunk the way Steam does: both streams written as <c>.tmp</c>, then renamed.</summary>
    public void AddChunk(string folder, int number, DateTime? writeTimeUtc = null)
    {
        foreach (var stream in new[] { 0, 1 })
        {
            var final = Path.Combine(folder, FolderNames.ChunkFile(stream, number));
            var tmp = final + ".tmp";
            File.Copy(Path.Combine(BaseFolder, FolderNames.ChunkFile(stream, number)), tmp, overwrite: true);
            File.SetLastWriteTimeUtc(tmp, writeTimeUtc ?? DateTime.UtcNow);
            File.Move(tmp, final, overwrite: true);
        }
    }

    public void CopyManifest(string folder, DateTime writeTimeUtc) => CopyOne("session.mpd", folder, writeTimeUtc);

    public void ZeroManifest(string folder, DateTime writeTimeUtc)
    {
        var p = Path.Combine(folder, "session.mpd");
        var len = File.Exists(Path.Combine(BaseFolder, "session.mpd")) ? new FileInfo(Path.Combine(BaseFolder, "session.mpd")).Length : 1024;
        File.WriteAllBytes(p, new byte[len]);
        File.SetLastWriteTimeUtc(p, writeTimeUtc);
    }

    private static void WriteText(string folder, string name, DateTime writeTimeUtc, string text)
    {
        var p = Path.Combine(folder, name);
        File.WriteAllText(p, text, new UTF8Encoding(false));
        File.SetLastWriteTimeUtc(p, writeTimeUtc);
    }

    /// <summary>Overwrites a chunk with zero bytes of the same length (Steam leaves such chunks behind under their normal names).</summary>
    public static void ZeroFill(string folder, int stream, int chunk)
    {
        var p = Path.Combine(folder, FolderNames.ChunkFile(stream, chunk));
        var len = new FileInfo(p).Length;
        var t = File.GetLastWriteTimeUtc(p);
        File.WriteAllBytes(p, new byte[len]);
        File.SetLastWriteTimeUtc(p, t);
    }

    /// <summary>Deletes both streams of a chunk (Steam's cleanup). Only ever call this on a folder this class made.</summary>
    public static void DeleteChunk(string folder, int number)
    {
        foreach (var stream in new[] { 0, 1 })
        {
            try { File.Delete(Path.Combine(folder, FolderNames.ChunkFile(stream, number))); }
            catch (IOException) { }
        }
    }

    // ---- timers ---------------------------------------------------------------------------------------------------

    /// <summary>Appends chunk <paramref name="firstNew"/>, <paramref name="firstNew"/> + 1, ... to <paramref name="folder"/> every <paramref name="interval"/> until disposed or the content ends.</summary>
    public ChunkTimer StartLive(string folder, int firstNew, TimeSpan interval) =>
        new(interval, firstNew, Chunks, n => AddChunk(folder, n));

    /// <summary>Deletes chunk <paramref name="firstOld"/>, <paramref name="firstOld"/> + 1, ... of <paramref name="folder"/> every <paramref name="interval"/> (Steam's head trim), the first one at once.</summary>
    public ChunkTimer StartHeadTrim(string folder, int firstOld, int lastToDelete, TimeSpan interval) =>
        new(interval, firstOld, lastToDelete, n => DeleteChunk(folder, n), immediate: true);

    public void Dispose()
    {
        try { Directory.Delete(Root, recursive: true); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { /* a handle still open: the OS temp cleaner gets it */ }
    }
}

/// <summary>Calls an action with consecutive chunk numbers on a fixed interval from a background thread; raises <see cref="Done"/> after each call.</summary>
public sealed class ChunkTimer : IDisposable
{
    private readonly CancellationTokenSource _cts = new();
    private readonly Task _task;

    internal ChunkTimer(TimeSpan interval, int first, int last, Action<int> action, bool immediate = false)
    {
        _task = Task.Run(async () =>
        {
            try
            {
                for (var n = first; n <= last; n++)
                {
                    if (!(immediate && n == first)) await Task.Delay(interval, _cts.Token).ConfigureAwait(false);
                    action(n);
                    LastNumber = n;
                    Done?.Invoke(n);
                }
            }
            catch (OperationCanceledException) { }
        });
    }

    /// <summary>The last chunk number the action ran for (0 before the first call).</summary>
    public int LastNumber { get; private set; }

    /// <summary>Raised on the timer thread after the action ran for a chunk number.</summary>
    public event Action<int>? Done;

    public void Dispose()
    {
        _cts.Cancel();
        try { _task.Wait(TimeSpan.FromSeconds(5)); } catch (AggregateException) { }
        _cts.Dispose();
    }
}
