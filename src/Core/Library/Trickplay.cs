// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;
using System.Globalization;
using System.Text.RegularExpressions;
using System.Threading.Channels;
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Library;

/// <param name="TileWidth">Thumbnail width in pixels (height follows the video's aspect ratio, 160 × 90 for 16:9).</param>
/// <param name="JpegQuality">ffmpeg <c>-q:v</c> scale (2 best … 31 worst); spike S4: 10 at 160 px = 3.5 KB per thumbnail.</param>
/// <param name="FfmpegPath">Override; default is FastEncoder's <c>Ff</c> lookup (next to the exe, PATH, winget).</param>
/// <param name="ReadAheadChunks">Fragments read ahead of the ffmpeg pipe (memory: about 0.3 MB each).</param>
public sealed record TrickplayOptions(
    DecodeMode Mode = DecodeMode.Gpu, int TileWidth = 160, int Columns = 10, int Rows = 10, int JpegQuality = 10,
    bool BelowNormalPriority = true, string? FfmpegPath = null, int ReadAheadChunks = 16);

/// <param name="Total">Chunks this run works on.</param>
/// <param name="Fed">Fragments handed to ffmpeg so far.</param>
/// <param name="Skipped">Chunks skipped so far (zero-filled, vanished, truncated, unreadable).</param>
public sealed record TrickplayProgress(int Total, int Fed, int Skipped);

/// <param name="NewChunks">Chunks that got a thumbnail for the first time (or were retried after being missing).</param>
/// <param name="Rebuilt">Chunks of the previously open (partial) last sheet that were decoded again to refill it.</param>
/// <param name="SheetsWritten">Sheet files written or replaced.</param>
/// <param name="Missing">Chunks skipped or without a frame in this run, with the reason.</param>
public sealed record TrickplayResult(
    int NewChunks, int Rebuilt, int SheetsWritten, IReadOnlyList<MissingChunk> Missing, DecodeMode UsedMode, TimeSpan Elapsed);

/// <summary>What a run will do, decided from the manifest on disk and the chunks the session has now.</summary>
public sealed record TrickplayPlan(IReadOnlyList<TrickplaySheet> KeptSheets, IReadOnlyList<int> Work, int NewChunks, int Rebuilt);

/// <summary>
/// Trickplay generator (plan Phase 2; spike S4). For each complete chunk of one <see cref="SessionMedia"/> it reads the init segment
/// plus the first fragment (the IDR) with the adaptive read, feeds them to one long-lived ffmpeg that decodes keyframes only, scales to
/// <see cref="TrickplayOptions.TileWidth"/> and tiles them into JPEG sprite sheets, written under <c>&lt;data&gt;/trick/&lt;key&gt;/</c> together
/// with <c>manifest.json</c> (<see cref="TrickplayManifest"/>; read it with <see cref="TrickplayIndex"/>).
/// <para>
/// Incremental: chunks already in the manifest are not touched. The last sheet, if it holds fewer tiles than its capacity, is rebuilt
/// together with the new chunks (its chunks are decoded again, at most capacity − 1 of them). Cancellation stops feeding, lets ffmpeg
/// flush, commits what was produced and then throws <see cref="OperationCanceledException"/>; a later run resumes with the rest.
/// Files in Steam's tree are opened read-only with Read | Write | Delete sharing; a chunk that vanishes is reported, not fatal.
/// Only <c>trick/&lt;key&gt;/</c> is written (sheets and manifest atomically via temp + rename); the only deletion is the run's own temp folder.
/// </para>
/// </summary>
public static partial class Trickplay
{
    public const int Stream = 0;

    /// <summary>The folder of one recording: <c>&lt;data&gt;/trick/&lt;recordingKey&gt;</c>. The key becomes a folder name, so it is validated.</summary>
    public static string DirFor(DataFolder data, string recordingKey)
    {
        if (!IsSafeKey(recordingKey)) throw new ArgumentException("recordingKey must be 1-128 of A-Z a-z 0-9 . _ - and not '.' or '..'", nameof(recordingKey));
        return Path.Combine(data.TrickDir, recordingKey);
    }

    public static bool IsSafeKey(string? key) =>
        !string.IsNullOrEmpty(key) && key.Length <= 128 && key is not ("." or "..")
        && key.All(c => char.IsAsciiLetterOrDigit(c) || c is '.' or '_' or '-');

    /// <summary>True when an ffmpeg can be located (<paramref name="ffmpegPath"/>, else FastEncoder's <c>Ff</c>).</summary>
    public static bool FfmpegAvailable(string? ffmpegPath = null) => ResolveFfmpeg(ffmpegPath) is not null;

    private static string? ResolveFfmpeg(string? ffmpegPath)
    {
        if (!string.IsNullOrEmpty(ffmpegPath)) return File.Exists(ffmpegPath) ? ffmpegPath : null;
        if (Ff.Ffmpeg.Length == 0) Ff.Locate();
        return Ff.Ffmpeg.Length > 0 ? Ff.Ffmpeg : null;
    }

    // ------------------------------------------------------------------ pure parts

    /// <summary>Decides the work: new chunks (not in any sheet) plus, when there are new ones, the chunks of the open last sheet that still exist.</summary>
    public static TrickplayPlan Plan(TrickplayManifest manifest, IEnumerable<int> sessionChunks)
    {
        var media = new SortedSet<int>(sessionChunks);
        var known = new HashSet<int>(manifest.Sheets.SelectMany(s => s.Chunks).Where(c => c > 0));
        var fresh = media.Where(c => !known.Contains(c)).ToList();
        if (fresh.Count == 0) return new(manifest.Sheets, [], 0, 0);

        var kept = manifest.Sheets;
        var rebuilt = new List<int>();
        if (kept.Count > 0 && kept[^1].Chunks.Count < manifest.Capacity)
        {
            rebuilt = [.. kept[^1].Chunks.Where(c => c > 0 && media.Contains(c))];
            kept = [.. kept.Take(kept.Count - 1)];
        }
        var work = rebuilt.Concat(fresh).Distinct().Order().ToList();
        return new(kept, work, fresh.Count, rebuilt.Count);
    }

    /// <summary>
    /// Attributes ffmpeg's output frames (in order, with their pts in seconds) to the chunks that were fed. Same count: order wins.
    /// Different count (a decode error dropped frames): the pts decides (chunk N has pts ≈ (N − 1) · segment); an unattributable frame
    /// becomes 0 (it still occupies its tile).
    /// </summary>
    public static int[] MapFrames(IReadOnlyList<int> fed, IReadOnlyList<double> framePts, double segmentSeconds)
    {
        var result = new int[framePts.Count];
        if (framePts.Count == fed.Count)
        {
            for (var i = 0; i < result.Length; i++) result[i] = fed[i];
            return result;
        }
        var feedSet = new HashSet<int>(fed);
        var used = new HashSet<int>();
        for (var i = 0; i < result.Length; i++)
        {
            var p = framePts[i];
            if (double.IsNaN(p) || double.IsInfinity(p)) continue;
            var c = (int)Math.Round(p / segmentSeconds) + 1;
            if (feedSet.Contains(c) && used.Add(c)) result[i] = c;
        }
        return result;
    }

    /// <summary>ffmpeg arguments (all numbers invariant). Input is fMP4 on stdin; output is <c>sheet-NNNN.jpg</c> starting at <paramref name="startNumber"/>.</summary>
    public static IReadOnlyList<string> BuildFfmpegArgs(
        DecodeMode mode, int tileWidth, int tileHeight, int columns, int rows, int quality, int startNumber, string outputPattern)
    {
        var inv = CultureInfo.InvariantCulture;
        var size = tileHeight > 0 ? string.Create(inv, $"{tileWidth}:{tileHeight}") : string.Create(inv, $"{tileWidth}:-2");
        List<string> a = ["-hide_banner", "-loglevel", "info", "-nostats"];
        if (mode == DecodeMode.Gpu) a.AddRange(["-hwaccel", "d3d11va"]);
        a.AddRange(["-skip_frame", "nokey", "-i", "pipe:0", "-an", "-fps_mode", "passthrough",
            "-vf", string.Create(inv, $"scale={size}:flags=fast_bilinear,format=yuvj420p,showinfo,tile={columns}x{rows}"),
            "-c:v", "mjpeg", "-q:v", quality.ToString(inv), "-start_number", startNumber.ToString(inv), "-f", "image2", outputPattern]);
        return a;
    }

    /// <summary>Width and height from a JPEG's SOF marker (reads only the header). False for anything that is not a baseline/progressive JPEG.</summary>
    public static bool TryGetJpegSize(ReadOnlySpan<byte> jpeg, out int width, out int height)
    {
        width = height = 0;
        if (jpeg.Length < 4 || jpeg[0] != 0xFF || jpeg[1] != 0xD8) return false;
        var p = 2;
        while (p + 4 <= jpeg.Length)
        {
            if (jpeg[p] != 0xFF) return false;
            var marker = jpeg[p + 1];
            if (marker == 0xFF) { p++; continue; }
            if (marker is 0x01 or (>= 0xD0 and <= 0xD8)) { p += 2; continue; }
            var len = jpeg[p + 2] << 8 | jpeg[p + 3];
            if (marker is >= 0xC0 and <= 0xCF and not (0xC4 or 0xC8 or 0xCC))
            {
                if (p + 9 > jpeg.Length) return false;
                height = jpeg[p + 5] << 8 | jpeg[p + 6];
                width = jpeg[p + 7] << 8 | jpeg[p + 8];
                return width > 0 && height > 0;
            }
            p += 2 + len;
        }
        return false;
    }

    // ------------------------------------------------------------------ fragment reader

    /// <summary>
    /// Adaptive read of the first fragment: <see cref="FirstFragment.DefaultFirstRead"/> bytes, then one exact second read when the IDR is
    /// larger. Returns the bytes <c>[moof … end of mdat)</c>, or the reason there are none. Never throws except for cancellation.
    /// </summary>
    public static async Task<(byte[]? Data, MissingReason Reason)> ReadFirstFragmentAsync(string path, CancellationToken ct)
    {
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.Asynchronous);
            var length = RandomAccess.GetLength(h);
            var buf = new byte[(int)Math.Min(length, FirstFragment.DefaultFirstRead)];
            var have = await FillAsync(h, buf, 0, ct).ConfigureAwait(false);
            for (var round = 0; round < 4; round++)
            {
                var info = FirstFragment.Find(buf.AsSpan(0, have));
                switch (info.Status)
                {
                    case FragmentStatus.Found:
                        return (buf.AsSpan((int)info.Offset, (int)info.Length).ToArray(), default);
                    case FragmentStatus.ZeroFilled: return (null, MissingReason.ZeroFilled);
                    case FragmentStatus.Invalid: return (null, MissingReason.Invalid);
                }
                if (info.RequiredBytes > length || have >= length) return (null, MissingReason.Truncated);
                if (info.RequiredBytes > FirstFragment.MaxFragmentBytes + FirstFragment.DefaultFirstRead) return (null, MissingReason.Invalid);
                var target = (int)Math.Max(info.RequiredBytes, have + 1);
                Array.Resize(ref buf, target);
                have = await FillAsync(h, buf, have, ct).ConfigureAwait(false);
            }
            return (null, MissingReason.Invalid);
        }
        catch (FileNotFoundException) { return (null, MissingReason.Vanished); }
        catch (DirectoryNotFoundException) { return (null, MissingReason.Vanished); }
        catch (OperationCanceledException) { throw; }
        catch (IOException) { return (null, MissingReason.Unreadable); }
        catch (UnauthorizedAccessException) { return (null, MissingReason.Unreadable); }
    }

    private static async Task<int> FillAsync(Microsoft.Win32.SafeHandles.SafeFileHandle h, byte[] buf, int have, CancellationToken ct)
    {
        while (have < buf.Length)
        {
            var n = await RandomAccess.ReadAsync(h, buf.AsMemory(have), have, ct).ConfigureAwait(false);
            if (n == 0) break;
            have += n;
        }
        return have;
    }

    // ------------------------------------------------------------------ generator

    /// <summary>
    /// Generates (or extends) the trickplay of <paramref name="media"/> under <c>&lt;data&gt;/trick/&lt;recordingKey&gt;/</c>.
    /// </summary>
    /// <exception cref="FileNotFoundException">No ffmpeg, or the session's init segment is missing.</exception>
    /// <exception cref="InvalidOperationException">ffmpeg failed in every mode without producing a frame.</exception>
    /// <exception cref="OperationCanceledException">Cancelled; what was produced so far is committed.</exception>
    public static async Task<TrickplayResult> GenerateAsync(
        DataFolder data, string recordingKey, SessionMedia media, TrickplayOptions? options = null,
        IProgress<TrickplayProgress>? progress = null, CancellationToken ct = default)
    {
        options ??= new TrickplayOptions();
        var started = Stopwatch.StartNew();
        var dir = DirFor(data, recordingKey);
        var exe = ResolveFfmpeg(options.FfmpegPath) ?? throw new FileNotFoundException("ffmpeg not found");
        if (options.TileWidth < 16 || options.Columns < 1 || options.Rows < 1) throw new ArgumentException("invalid tile geometry", nameof(options));

        data.EnsureCreated();
        Directory.CreateDirectory(dir);
        var old = TrickplayManifest.TryRead(dir);
        // An existing manifest fixes the geometry: its sheets and the new ones must agree.
        var manifest = old ?? TrickplayManifest.Empty(options.TileWidth, options.Columns, options.Rows, media.SegmentSeconds);
        var plan = Plan(manifest, media.Chunks());
        if (plan.Work.Count == 0)
            return new(0, 0, 0, [], options.Mode, started.Elapsed);

        var initPath = Path.Combine(media.Folder, FolderNames.InitFile(Stream));
        byte[] init;
        try { init = await ReadAllSharedAsync(initPath, ct).ConfigureAwait(false); }
        catch (DirectoryNotFoundException e) { throw new FileNotFoundException("Init segment not found.", initPath, e); }

        var firstSheet = plan.KeptSheets.Count;
        var mode = options.Mode;
        Attempt attempt;
        while (true)
        {
            attempt = await RunAttemptAsync(exe, mode, options, manifest, media, plan.Work, init, firstSheet, dir, progress, ct).ConfigureAwait(false);
            var failed = attempt.Fed.Count > 0 && !attempt.Cancelled && (attempt.ExitCode != 0 || attempt.FramePts.Count == 0);
            if (!failed) break;
            if (mode == DecodeMode.Gpu) { mode = DecodeMode.Cpu; continue; }
            if (attempt.FramePts.Count == 0)
            {
                TryDeleteDir(attempt.TempDir);
                throw new InvalidOperationException($"ffmpeg exited with code {attempt.ExitCode} without producing a frame: {attempt.StderrTail}");
            }
            break; // some frames made it: keep them, the rest is reported as NoFrame
        }

        var mapped = MapFrames(attempt.Fed, attempt.FramePts, manifest.IntervalSeconds);
        var tileHeight = manifest.TileHeight > 0 ? manifest.TileHeight : attempt.TileHeight;
        var newSheets = new List<TrickplaySheet>();
        var capacity = manifest.Capacity;
        for (var s = 0; s * capacity < mapped.Length; s++)
            newSheets.Add(new(SheetName(firstSheet + s), mapped.Skip(s * capacity).Take(capacity).ToArray()));

        var got = new HashSet<int>(mapped.Where(c => c > 0));
        var missing = attempt.Missing.ToList();
        missing.AddRange(attempt.Fed.Where(c => !got.Contains(c)).Select(c => new MissingChunk(c, MissingReason.NoFrame)));

        var all = new List<TrickplaySheet>(plan.KeptSheets);
        all.AddRange(newSheets);
        var present = new HashSet<int>(all.SelectMany(s => s.Chunks).Where(c => c > 0));
        var thisRun = new HashSet<int>(plan.Work);
        var carried = manifest.Missing.Where(m => !present.Contains(m.Chunk) && !thisRun.Contains(m.Chunk));
        var finalMissing = carried.Concat(missing).OrderBy(m => m.Chunk).ToList();

        // Sheets first, manifest last: a reader that sees the manifest finds every sheet it names.
        foreach (var f in attempt.SheetFiles) File.Move(f.Temp, Path.Combine(dir, SheetName(f.Index)), overwrite: true);
        var committed = manifest with { TileHeight = tileHeight, Sheets = all, Missing = finalMissing };
        if (newSheets.Count > 0 || !finalMissing.SequenceEqual(manifest.Missing)) committed.Write(dir);
        TryDeleteDir(attempt.TempDir);

        ct.ThrowIfCancellationRequested();
        var openBefore = plan.Rebuilt > 0 ? manifest.Sheets[^1].Chunks.ToHashSet() : [];
        return new(got.Count(c => !openBefore.Contains(c)), plan.Rebuilt, attempt.SheetFiles.Count, missing, mode, started.Elapsed);
    }

    private static string SheetName(int index) => string.Create(CultureInfo.InvariantCulture, $"sheet-{index:0000}.jpg");

    private sealed class Attempt
    {
        public List<int> Fed { get; } = [];
        public List<double> FramePts { get; } = [];
        public List<MissingChunk> Missing { get; } = [];
        public List<(int Index, string Temp)> SheetFiles { get; } = [];
        public int ExitCode { get; set; }
        public string StderrTail { get; set; } = "";
        public bool Cancelled { get; set; }
        public int TileHeight { get; set; }
        public string TempDir { get; set; } = "";
    }

    [GeneratedRegex(@"pts_time:\s*(-?[0-9.]+|N/A)", RegexOptions.CultureInvariant)]
    private static partial Regex PtsTimeRegex();

    private static async Task<Attempt> RunAttemptAsync(
        string exe, DecodeMode mode, TrickplayOptions opt, TrickplayManifest manifest, SessionMedia media, IReadOnlyList<int> work,
        byte[] init, int firstSheet, string dir, IProgress<TrickplayProgress>? progress, CancellationToken ct)
    {
        var result = new Attempt { TempDir = Path.Combine(dir, ".tmp-" + Guid.NewGuid().ToString("N")) };
        Directory.CreateDirectory(result.TempDir);
        var args = BuildFfmpegArgs(mode, manifest.TileWidth, manifest.TileHeight, manifest.Columns, manifest.Rows, opt.JpegQuality,
            firstSheet, Path.Combine(result.TempDir, "sheet-%04d.jpg"));
        var psi = Ff.StartInfo(exe, args);
        psi.RedirectStandardInput = true;
        using var proc = new Process { StartInfo = psi };
        proc.Start();
        _ = proc.StandardOutput.ReadToEndAsync(CancellationToken.None); // ffmpeg writes images to files; just never let the pipe fill
        if (opt.BelowNormalPriority)
            try { proc.PriorityClass = ProcessPriorityClass.BelowNormal; } catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception) { }

        var tail = new Queue<string>();
        var stderr = Task.Run(async () =>
        {
            string? line;
            while ((line = await proc.StandardError.ReadLineAsync().ConfigureAwait(false)) is not null)
            {
                if (line.Contains("showinfo", StringComparison.Ordinal) && PtsTimeRegex().Match(line) is { Success: true } m)
                {
                    lock (result.FramePts)
                        result.FramePts.Add(double.TryParse(m.Groups[1].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) ? v : double.NaN);
                    continue;
                }
                lock (tail) { tail.Enqueue(line); if (tail.Count > 12) tail.Dequeue(); }
            }
        }, CancellationToken.None);

        var total = work.Count;
        var skipped = 0;
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var channel = Channel.CreateBounded<(int Chunk, byte[] Data)>(Math.Max(1, opt.ReadAheadChunks));
        var producer = Task.Run(async () =>
        {
            try
            {
                foreach (var chunk in work)
                {
                    stop.Token.ThrowIfCancellationRequested();
                    var (bytes, reason) = await ReadFirstFragmentAsync(Path.Combine(media.Folder, FolderNames.ChunkFile(Stream, chunk)), stop.Token).ConfigureAwait(false);
                    if (bytes is null)
                    {
                        lock (result.Missing) result.Missing.Add(new(chunk, reason));
                        progress?.Report(new(total, result.Fed.Count, Interlocked.Increment(ref skipped)));
                        continue;
                    }
                    await channel.Writer.WriteAsync((chunk, bytes), stop.Token).ConfigureAwait(false);
                }
            }
            catch (OperationCanceledException) { }
            finally { channel.Writer.TryComplete(); }
        }, CancellationToken.None);

        try
        {
            var stdin = proc.StandardInput.BaseStream;
            try
            {
                await stdin.WriteAsync(init, ct).ConfigureAwait(false);
                await foreach (var (chunk, bytes) in channel.Reader.ReadAllAsync(CancellationToken.None).ConfigureAwait(false))
                {
                    if (ct.IsCancellationRequested) break;
                    await stdin.WriteAsync(bytes, ct).ConfigureAwait(false);
                    await stdin.FlushAsync(ct).ConfigureAwait(false);
                    result.Fed.Add(chunk);
                    progress?.Report(new(total, result.Fed.Count, Volatile.Read(ref skipped)));
                }
            }
            catch (OperationCanceledException) { }
            catch (IOException) { /* ffmpeg ended early; its exit code and stderr tell why */ }
            finally
            {
                await stop.CancelAsync().ConfigureAwait(false);
                try { stdin.Close(); } catch (IOException) { }
            }
            await producer.ConfigureAwait(false);
            result.Cancelled = ct.IsCancellationRequested;

            using var grace = new CancellationTokenSource(TimeSpan.FromSeconds(result.Cancelled ? 20 : 120));
            try { await proc.WaitForExitAsync(grace.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) { try { proc.Kill(true); } catch (InvalidOperationException) { } await proc.WaitForExitAsync(CancellationToken.None).ConfigureAwait(false); }
            await stderr.ConfigureAwait(false);
            result.ExitCode = proc.ExitCode;
        }
        catch
        {
            try { if (!proc.HasExited) proc.Kill(true); } catch (InvalidOperationException) { }
            throw;
        }
        lock (tail) result.StderrTail = string.Join(" | ", tail);

        var sheetCount = (result.FramePts.Count + manifest.Capacity - 1) / manifest.Capacity;
        for (var s = 0; s < sheetCount; s++)
        {
            var temp = Path.Combine(result.TempDir, SheetName(firstSheet + s));
            if (File.Exists(temp)) result.SheetFiles.Add((firstSheet + s, temp));
        }
        if (result.SheetFiles.Count > 0 && manifest.TileHeight == 0)
        {
            var head = new byte[64 * 1024];
            await using var fs = new FileStream(result.SheetFiles[0].Temp, FileMode.Open, FileAccess.Read, FileShare.Read);
            var n = await fs.ReadAsync(head, CancellationToken.None).ConfigureAwait(false);
            if (TryGetJpegSize(head.AsSpan(0, n), out _, out var h)) result.TileHeight = h / manifest.Rows;
        }
        return result;
    }

    private static async Task<byte[]> ReadAllSharedAsync(string path, CancellationToken ct)
    {
        await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, 4096, useAsync: true);
        var buf = new byte[fs.Length];
        await fs.ReadExactlyAsync(buf, ct).ConfigureAwait(false);
        return buf;
    }

    private static void TryDeleteDir(string dir)
    {
        try { if (dir.Length > 0) Directory.Delete(dir, recursive: true); }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
}
