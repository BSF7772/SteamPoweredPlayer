// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S4 (trickplay-cost). Throwaway. Two modes:
//   scan   <sessionDir> <out.csv> [readBytes=2097152]
//          reads the head of every chunk-stream0 file (read-only, delete-sharing), parses moof/trun and records where the first
//          sample (the IDR) ends, i.e. the minimum prefix that holds the whole IDR. Prints failure counts per prefix size.
//   decode <sessionDir> <cpu|d3d11va|cuda|cuda-scale> <prefixBytes> <raw|jpg|webp> <outDir> [limit=0] [step=1] [threads=0]
//          feeds init + (moof + first sample of each chunk, rebuilt as a one-sample fragment) to ONE long-lived ffmpeg,
//          keyframe-only, scaled to 320x180. raw = frames to stdout (timing per frame); jpg/webp = 10x10 sprite sheets.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Channels;

namespace TrickplaySpike;

public static partial class Program
{
    [GeneratedRegex(@"^chunk-stream0-(\d+)\.m4s$")]
    private static partial Regex ChunkRx();

    static readonly int ThumbW = int.Parse(Environment.GetEnvironmentVariable("THUMB_W") ?? "320", CultureInfo.InvariantCulture);
    static readonly int ThumbH = ThumbW * 9 / 16 / 2 * 2; // 16:9, even
    static readonly string JpgQ = Environment.GetEnvironmentVariable("JPG_Q") ?? "5";
    static readonly string WebpQ = Environment.GetEnvironmentVariable("WEBP_Q") ?? "60";

    public static async Task<int> Main(string[] args)
    {
        try
        {
            if (args.Length >= 3 && args[0] == "scan") return Scan(args[1], args[2], args.Length > 3 ? int.Parse(args[3], CultureInfo.InvariantCulture) : 2 * 1024 * 1024);
            if (args.Length >= 6 && args[0] == "decode")
                return await Decode(args[1], args[2], int.Parse(args[3], CultureInfo.InvariantCulture), args[4], args[5],
                    args.Length > 6 ? int.Parse(args[6], CultureInfo.InvariantCulture) : 0,
                    args.Length > 7 ? int.Parse(args[7], CultureInfo.InvariantCulture) : 1,
                    args.Length > 8 ? int.Parse(args[8], CultureInfo.InvariantCulture) : 0);
        }
        catch (Exception e) { Console.Error.WriteLine(e); return 2; }
        Console.Error.WriteLine("usage: scan <dir> <csv> [bytes] | decode <dir> <cpu|d3d11va|cuda|cuda-scale> <prefix> <raw|jpg|webp> <outDir> [limit] [step] [threads]");
        return 1;
    }

    static List<(int N, string Path)> Chunks(string dir) =>
        Directory.EnumerateFiles(dir, "chunk-stream0-*.m4s")
            .Select(p => (m: ChunkRx().Match(Path.GetFileName(p)), p)).Where(x => x.m.Success)
            .Select(x => (int.Parse(x.m.Groups[1].Value, CultureInfo.InvariantCulture), x.p)).OrderBy(x => x.Item1).ToList();

    /// <summary>Reads at most n bytes from the start of a file; read+write+delete sharing so Steam's cleanup never fails.</summary>
    static byte[]? ReadPrefix(string path, int n, out long fileLen, out double ms)
    {
        fileLen = 0; ms = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            fileLen = RandomAccess.GetLength(h);
            var buf = new byte[(int)Math.Min(n, fileLen)];
            int got = 0;
            while (got < buf.Length)
            {
                int r = RandomAccess.Read(h, buf.AsSpan(got), got);
                if (r <= 0) break;
                got += r;
            }
            ms = sw.Elapsed.TotalMilliseconds;
            return got == buf.Length ? buf : buf[..got];
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException)
        {
            return null; // vanished mid-read (Steam trimmed it)
        }
    }


    /// <summary>Adaptive read: 4 KB for moof + mdat header, then exactly the rest of the first fragment (size comes from the moof).</summary>
    static byte[]? ReadFirstFragment(string path, out long fileLen, out double ms)
    {
        fileLen = 0; ms = 0;
        var sw = Stopwatch.StartNew();
        try
        {
            using var h = File.OpenHandle(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete, FileOptions.SequentialScan);
            fileLen = RandomAccess.GetLength(h);
            var buf = new byte[(int)Math.Min(4096, fileLen)];
            int got = RandomAccess.Read(h, buf, 0);
            if (got < buf.Length) buf = buf[..got];
            var ci = Fmp4.Parse(buf, out _);
            if (ci != null && ci.RequiredPrefix > buf.Length && ci.RequiredPrefix <= Math.Min(fileLen, 8 << 20))
            {
                var full = new byte[ci.RequiredPrefix];
                buf.CopyTo(full, 0);
                int g = buf.Length;
                while (g < full.Length) { int r = RandomAccess.Read(h, full.AsSpan(g), g); if (r <= 0) break; g += r; }
                buf = g == full.Length ? full : full[..g];
            }
            ms = sw.Elapsed.TotalMilliseconds;
            return buf;
        }
        catch (Exception e) when (e is FileNotFoundException or DirectoryNotFoundException or IOException) { return null; }
    }
    static double Pct(List<double> sorted, double p) => sorted.Count == 0 ? double.NaN : sorted[(int)Math.Min(sorted.Count - 1, Math.Floor(p * (sorted.Count - 1) + 0.5))];

    static int Scan(string dir, string csv, int readBytes)
    {
        var chunks = Chunks(dir);
        var sb = new StringBuilder("chunk,fileLen,moofSize,sampleStart,idrSize,requiredPrefix,firstSync,samples,mdatSize,nalTypes,nalExact,vclNals,readMs\n");
        var required = new List<double>(); var idr = new List<double>(); var readMs = new List<double>(); var fileLens = new List<double>();
        int parseFail = 0, notSync = 0, beyondRead = 0, nalBad = 0, vanished = 0, multiSync = 0;
        var errors = new Dictionary<string, int>();
        long totalFile = 0;
        foreach (var (n, path) in chunks)
        {
            var head = ReadPrefix(path, readBytes, out var len, out var ms);
            if (head == null) { vanished++; continue; }
            totalFile += len; fileLens.Add(len); readMs.Add(ms);
            var ci = Fmp4.Parse(head, out var err);
            if (ci == null) { parseFail++; errors[err] = errors.GetValueOrDefault(err) + 1; sb.Append(CultureInfo.InvariantCulture, $"{n},{len},,,,,,,,,,,{ms:F2}\n"); continue; }
            if (!ci.FirstIsSync) notSync++;
            bool mdatMismatch = ci.SampleCount == 1 && ci.MdatSize - 8 != ci.FirstSampleSize; if (mdatMismatch) multiSync++;
            string nals = ""; bool exact = false; int vcl = 0;
            if (ci.RequiredPrefix <= head.Length)
            {
                var (types, ex, v) = Fmp4.WalkNals(head.AsSpan((int)ci.FirstSampleOffset, ci.FirstSampleSize));
                nals = string.Join(' ', types); exact = ex; vcl = v;
                if (!ex || v == 0) nalBad++;
            }
            else beyondRead++;
            required.Add(ci.RequiredPrefix); idr.Add(ci.FirstSampleSize);
            sb.Append(CultureInfo.InvariantCulture, $"{n},{len},{ci.MoofSize},{ci.FirstSampleOffset},{ci.FirstSampleSize},{ci.RequiredPrefix},{ci.FirstIsSync},{ci.SampleCount},{ci.MdatSize},{nals},{exact},{vcl},{ms:F2}\n");
        }
        File.WriteAllText(csv, sb.ToString());
        required.Sort(); idr.Sort(); readMs.Sort(); fileLens.Sort();
        Console.WriteLine($"chunks listed={chunks.Count} parsed={required.Count} vanished={vanished} parseFail={parseFail} firstNotSync={notSync} mdatSizeMismatch={multiSync} idrBeyondReadWindow({readBytes}B)={beyondRead} nalWalkBad={nalBad}");
        foreach (var kv in errors) Console.WriteLine($"  parse error x{kv.Value}: {kv.Key}");
        Console.WriteLine($"chunk file size  KB: min={fileLens.FirstOrDefault() / 1024:F0} p50={Pct(fileLens, .5) / 1024:F0} max={fileLens.LastOrDefault() / 1024:F0}; total={totalFile / 1048576.0:F0} MB");
        Console.WriteLine($"IDR sample size  KB: min={idr.FirstOrDefault() / 1024:F1} p50={Pct(idr, .5) / 1024:F1} p90={Pct(idr, .9) / 1024:F1} p99={Pct(idr, .99) / 1024:F1} max={idr.LastOrDefault() / 1024:F1}");
        Console.WriteLine($"required prefix  KB: min={required.FirstOrDefault() / 1024:F1} p50={Pct(required, .5) / 1024:F1} p90={Pct(required, .9) / 1024:F1} p99={Pct(required, .99) / 1024:F1} p99.9={Pct(required, .999) / 1024:F1} max={required.LastOrDefault() / 1024:F1}  (moof + whole IDR)");
        foreach (var kb in new[] { 128, 192, 256, 320, 384, 448, 512, 640, 768, 1024 })
            Console.WriteLine($"  prefix {kb,5} KB: IDR not fully inside = {required.Count(r => r > kb * 1024)} / {required.Count}");
        readMs.RemoveAll(double.IsNaN);
        Console.WriteLine($"head read ({readBytes / 1024} KB window, includes open): p50={Pct(readMs, .5):F2} ms p95={Pct(readMs, .95):F2} ms max={readMs.LastOrDefault():F2} ms");
        return 0;
    }

    static string FfmpegArgs(string mode, string fmt, string outDir, int threads)
    {
        var hw = mode switch
        {
            "cpu" => "",
            "d3d11va" => "-hwaccel d3d11va ",
            "cuda" => "-hwaccel cuda ",
            "cuda-scale" => "-hwaccel cuda -hwaccel_output_format cuda ",
            _ => throw new ArgumentException("mode")
        };
        var thr = threads > 0 ? $"-threads {threads} " : "";
        var scale = mode == "cuda-scale"
            ? $"scale_cuda={ThumbW}:{ThumbH}:interp_algo=bilinear,hwdownload,format=nv12,format=yuvj420p"
            : $"scale={ThumbW}:{ThumbH}:flags=fast_bilinear";
        var common = $"-hide_banner -nostdin -v warning {thr}{hw}-i pipe:0 -an -sn -fps_mode passthrough ";
        return fmt switch
        {
            "raw" => $"{common}-vf {scale},format=yuvj420p -f rawvideo pipe:1",
            "jpg" => $"{common}-vf {scale},format=yuvj420p,tile=10x10 -c:v mjpeg -q:v {JpgQ} -f image2 \"{outDir}/sheet_%04d.jpg\"",
            "webp" => $"{common}-vf {scale},format=yuv420p,tile=10x10 -c:v libwebp -quality {WebpQ} -f image2 \"{outDir}/sheet_%04d.webp\"",
            _ => throw new ArgumentException("fmt")
        };
    }

    static async Task<int> Decode(string dir, string mode, int prefixBytes, string fmt, string outDir, int limit, int step, int threads)
    {
        Directory.CreateDirectory(outDir);
        var init = ReadPrefix(Path.Combine(dir, "init-stream0.m4s"), 1 << 20, out _, out _) ?? throw new IOException("no init segment");
        var chunks = Chunks(dir).Where((_, i) => i % step == 0).ToList();
        if (limit > 0) chunks = chunks.Take(limit).ToList();

        var psi = new ProcessStartInfo("ffmpeg", FfmpegArgs(mode, fmt, outDir, threads))
        {
            RedirectStandardInput = true, RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true
        };
        var errText = new StringBuilder();
        var swTotal = Stopwatch.StartNew();
        using var ff = Process.Start(psi)!;
        ff.ErrorDataReceived += (_, e) => { if (e.Data != null) lock (errText) errText.AppendLine(e.Data); };
        ff.BeginErrorReadLine();

        // frame arrival timestamps (raw mode) / drain stdout
        var arrivals = new List<double>();
        int frameBytes = ThumbW * ThumbH * 3 / 2;
        var readerTask = Task.Run(async () =>
        {
            var s = ff.StandardOutput.BaseStream; var buf = new byte[frameBytes]; int fill = 0;
            while (true)
            {
                int r = await s.ReadAsync(buf.AsMemory(fill));
                if (r <= 0) break;
                fill += r;
                if (fill == frameBytes) { lock (arrivals) arrivals.Add(swTotal.Elapsed.TotalMilliseconds); fill = 0; }
            }
        });

        // producer: read prefix, parse, rebuild a one-sample fragment
        var chan = Channel.CreateBounded<byte[]>(64);
        long bytesRead = 0; int fed = 0, failShort = 0, failParse = 0, vanished = 0; long fragBytes = 0;
        var readMs = new List<double>(); var failList = new List<string>();
        var producer = Task.Run(async () =>
        {
            foreach (var (n, path) in chunks)
            {
                var pre = prefixBytes > 0 ? ReadPrefix(path, prefixBytes, out var len, out var ms) : ReadFirstFragment(path, out len, out ms);
                if (pre == null) { vanished++; continue; }
                bytesRead += pre.Length; readMs.Add(ms);
                var ci = Fmp4.Parse(pre, out var err);
                if (ci == null) { failParse++; failList.Add($"{n}: parse: {err}"); continue; }
                var frag = ci.RequiredPrefix <= pre.Length ? pre.AsSpan(ci.MoofStart, (int)ci.RequiredPrefix - ci.MoofStart).ToArray() : null;
                if (frag == null) { failShort++; failList.Add($"{n}: IDR needs {ci.RequiredPrefix} B > prefix {pre.Length} B"); continue; }
                fragBytes += frag.Length; fed++;
                await chan.Writer.WriteAsync(frag);
            }
            chan.Writer.Complete();
        });

        double tFirstWrite = -1;
        var stdin = ff.StandardInput.BaseStream;
        try
        {
            await stdin.WriteAsync(init);
            await foreach (var frag in chan.Reader.ReadAllAsync())
            {
                if (tFirstWrite < 0) tFirstWrite = swTotal.Elapsed.TotalMilliseconds;
                await stdin.WriteAsync(frag);
            }
            await stdin.FlushAsync();
        }
        catch (IOException e) { Console.WriteLine("ffmpeg closed stdin early: " + e.Message); }
        stdin.Close();
        await producer;
        await readerTask;
        await ff.WaitForExitAsync();
        double wall = swTotal.Elapsed.TotalMilliseconds;
        ff.WaitForExit();
        double cpuSec = ff.TotalProcessorTime.TotalSeconds;

        Console.WriteLine($"mode={mode} fmt={fmt} prefix={prefixBytes} B threads={(threads == 0 ? "auto" : threads.ToString(CultureInfo.InvariantCulture))} chunks={chunks.Count} fed={fed} vanished={vanished} idrNotInPrefix={failShort} parseFail={failParse} ffmpegExit={ff.ExitCode}");
        foreach (var f in failList.Take(20)) Console.WriteLine("  FAIL " + f);
        int attempted = fed + failShort + failParse; double mbPerChunk = attempted == 0 ? 0.0 : bytesRead / (double)attempted / 1048576.0; double fragKb = fed == 0 ? 0.0 : fragBytes / (double)fed / 1024;
        Console.WriteLine($"bytes read from chunk files: {bytesRead / 1048576.0:F1} MB total = {mbPerChunk:F3} MB/chunk; fragments fed to ffmpeg: {fragBytes / 1048576.0:F1} MB ({fragKb:F0} KB/chunk)");
        readMs.Sort();
        Console.WriteLine($"prefix read (open+read): p50={Pct(readMs, .5):F2} ms p95={Pct(readMs, .95):F2} ms max={readMs.LastOrDefault():F2} ms");
        double feedSpan = wall - Math.Max(0, tFirstWrite);
        Console.WriteLine($"wall total={wall:F0} ms; time to first write (producer+ffmpeg spawn)={tFirstWrite:F0} ms; feed-to-exit={feedSpan:F0} ms => {(fed == 0 ? 0 : feedSpan / fed):F2} ms/chunk; ffmpeg CPU time={cpuSec:F2} s ({(fed == 0 ? 0 : cpuSec * 1000 / fed):F2} CPU-ms/chunk)");
        if (fmt == "raw")
        {
            List<double> a; lock (arrivals) a = [.. arrivals];
            Console.WriteLine($"frames out={a.Count} (expected {fed}); first frame at {(a.Count > 0 ? a[0] : 0):F0} ms");
            if (a.Count > 20)
            {
                var d = new List<double>();
                for (int i = 1; i < a.Count; i++) d.Add(a[i] - a[i - 1]);
                var steady = d.Skip(10).ToList(); steady.Sort();
                Console.WriteLine($"steady-state inter-frame time ms: mean={steady.Average():F2} p50={Pct(steady, .5):F2} p95={Pct(steady, .95):F2} p99={Pct(steady, .99):F2} max={steady.Last():F2}");
                Console.WriteLine($"2h extrapolation (2400 chunks x steady mean) = {2400 * steady.Average() / 1000:F1} s");
            }
        }
        else
        {
            var files = Directory.GetFiles(outDir, "sheet_*." + fmt);
            long sz = files.Sum(f => new FileInfo(f).Length);
            Console.WriteLine($"sheets={files.Length} total={sz / 1024.0:F0} KB avg={(files.Length == 0 ? 0 : sz / (double)files.Length / 1024):F0} KB/sheet = {(fed == 0 ? 0 : sz / (double)fed):F0} B/thumb");
            Console.WriteLine($"sprite size per hour of footage (step=1): {(fed == 0 ? 0 : sz / (fed * 3.0 / 3600.0) / 1048576.0):F2} MB/h");
        }
        lock (errText) { if (errText.Length > 0) { Console.WriteLine("ffmpeg stderr (first 3000 chars):"); Console.WriteLine(errText.ToString()[..Math.Min(3000, errText.Length)]); } }
        return 0;
    }
}
