// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S6: link FastEncoder Core, run Pipeline.EncodeAsync (GPU Turbo) on a lossless range file with an audio delay,
// and measure the A/V offset before and after.
//
//   FastEncoderLink <sessionDir> <firstChunk> <chunkCount> <outDir>
//
// <sessionDir> is only ever READ (share read+write+delete, a few ms per chunk). Everything written goes to <outDir>.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using FastEncoder.Core;

CultureInfo.DefaultThreadCurrentCulture = CultureInfo.InvariantCulture;
CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

if (args.Length < 4) { Console.Error.WriteLine("usage: FastEncoderLink <sessionDir> <firstChunk> <chunkCount> <outDir>"); return 2; }
var sessionDir = args[0];
int first = int.Parse(args[1], CultureInfo.InvariantCulture), count = int.Parse(args[2], CultureInfo.InvariantCulture);
var outDir = Path.GetFullPath(args[3]);
Directory.CreateDirectory(outDir);

var report = new StringBuilder();
void Log(string s = "") { Console.WriteLine(s); report.AppendLine(s); File.WriteAllText(Path.Combine(outDir, "report.txt"), report.ToString()); }
string F(double v, string f = "0.0") => v.ToString(f, CultureInfo.InvariantCulture);

// ---------------------------------------------------------------- 0. FastEncoder bootstrap
Ff.Locate();
if (!Ff.Available) { Log("FAIL ffmpeg/ffprobe not found"); return 1; }
var ver = await Ff.RunAsync(Ff.Ffmpeg, ["-version"]);
Log("ffmpeg: " + ver.StdOut.Split('\n')[0].Trim());

var turboBeforeDetect = Presets.Get("turbo")!.Build().Encoder;
await Encoders.DetectAsync();
var turbo = Presets.Get("turbo")!.Build();
Log($"GPU Turbo before Encoders.DetectAsync(): {turboBeforeDetect}   after: {turbo.Encoder} (q={turbo.Quality}, speed idx={turbo.Speed}, audio={turbo.Audio}@{turbo.AudioKbps}k, {turbo.Container})");
Log("encoders available: " + string.Join(", ", Encoders.Available.Select(e => e.Id)));
Log();

// ---------------------------------------------------------------- 1. lossless range (stage 1 of the export path)
var rangeFile = Path.Combine(outDir, "range.mp4");
{
    var vcat = Path.Combine(outDir, "video.mp4");
    var acat = Path.Combine(outDir, "audio.m4a");
    var sw = Stopwatch.StartNew();
    async Task Concat(string init, string pattern, string dest)
    {
        await using var o = new FileStream(dest, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20, FileOptions.Asynchronous);
        foreach (var part in new[] { init }.Concat(Enumerable.Range(first, count).Select(n => string.Format(CultureInfo.InvariantCulture, pattern, n))))
        {
            // read + write + delete sharing so Steam's own cleanup never fails because of us
            await using var i = new FileStream(Path.Combine(sessionDir, part), FileMode.Open, FileAccess.Read,
                FileShare.ReadWrite | FileShare.Delete, 1 << 20, FileOptions.Asynchronous | FileOptions.SequentialScan);
            await i.CopyToAsync(o);
        }
    }
    await Task.WhenAll(Concat("init-stream0.m4s", "chunk-stream0-{0:00000}.m4s", vcat), Concat("init-stream1.m4s", "chunk-stream1-{0:00000}.m4s", acat));
    var tConcat = sw.Elapsed.TotalSeconds;
    var mux = await Ff.RunAsync(Ff.Ffmpeg, ["-hide_banner", "-v", "error", "-i", vcat, "-i", acat, "-map", "0:v", "-map", "1:a",
        "-c", "copy", "-avoid_negative_ts", "make_zero", "-movflags", "+faststart", "-y", rangeFile]);
    if (mux.ExitCode != 0) { Log("FAIL range mux: " + mux.StdErr); return 1; }
    Log($"lossless range: {count} chunks ({count * 3} s): byte-concat {F(tConcat * 1000, "0")} ms, concat+remux {F(sw.Elapsed.TotalSeconds * 1000, "0")} ms (cold cache unknown), {new FileInfo(rangeFile).Length} B");
    // the byte-concatenated temp files stay in <outDir> (gitignored); cleanup is left to the maintainer
}

// ---------------------------------------------------------------- helpers
async Task<string> StreamStart(string file)
{
    var r = await Ff.RunAsync(Ff.Ffprobe, ["-v", "error", "-show_entries", "stream=codec_type,codec_name,pix_fmt,color_range,start_time,width,height,r_frame_rate",
        "-of", "compact=p=0:nk=0", file]);
    return string.Join(" | ", r.StdOut.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
}

/// <summary>Audio decoded so sample 0 == video time 0 (pads a late start), mono 16 kHz, then a 1 ms rectified envelope.</summary>
async Task<double[]> Envelope(string file)
{
    var psi = Ff.StartInfo(Ff.Ffmpeg, ["-hide_banner", "-nostdin", "-v", "error", "-i", file, "-map", "0:a:0", "-vn",
        "-af", "aresample=async=1:first_pts=0", "-ac", "1", "-ar", "16000", "-f", "f32le", "-"]);
    psi.StandardOutputEncoding = null;
    using var p = Process.Start(psi)!;
    var err = p.StandardError.ReadToEndAsync();
    using var ms = new MemoryStream();
    await p.StandardOutput.BaseStream.CopyToAsync(ms);
    await p.WaitForExitAsync();
    var s = new float[ms.Length / 4];
    Buffer.BlockCopy(ms.GetBuffer(), 0, s, 0, s.Length * 4);
    var env = new double[s.Length / 16];
    for (int i = 0; i < env.Length; i++) { double a = 0; for (int k = 0; k < 16; k++) a += Math.Abs(s[i * 16 + k]); env[i] = a / 16; }
    return env;
}

/// <summary>Objective check independent of the AudioSync heuristic: how far did the soundtrack move? +ms = later. ±1.5 s.</summary>
async Task<(double Ms, double Score)> AudioShift(string reference, string other)
{
    var a = await Envelope(reference); var b = await Envelope(other);
    int m = Math.Min(a.Length, b.Length), maxLag = 1500;
    double Z(double[] x, int n) { double mean = x.Take(n).Average(); return Math.Sqrt(x.Take(n).Sum(v => (v - mean) * (v - mean)) / n) + 1e-12; }
    var ma = a.Take(m).Average(); var mb = b.Take(m).Average();
    var cc = new double[2 * maxLag + 1];
    Parallel.For(-maxLag, maxLag + 1, l =>
    {
        double s = 0; int i0 = Math.Max(0, -l), i1 = m - Math.Max(0, l);
        for (int i = i0; i < i1; i++) s += (a[i] - ma) * (b[i + l] - mb);   // b later than a by l ms
        cc[l + maxLag] = s / Math.Max(1, i1 - i0);
    });
    int best = 0; for (int i = 1; i < cc.Length; i++) if (cc[i] > cc[best]) best = i;
    double norm = Z(a, m) * Z(b, m);
    return (best - maxLag, cc[best] / norm);
}

async Task<string> Inject(string src, string dst, double audioLateSeconds)
{
    // audio late by +x: shift the audio input forward with -itsoffset; early: -ss on the audio input
    var audioArgs = audioLateSeconds >= 0
        ? new[] { "-itsoffset", Ff.Sec(audioLateSeconds), "-i", src }
        : new[] { "-ss", Ff.Sec(-audioLateSeconds), "-i", src };
    var r = await Ff.RunAsync(Ff.Ffmpeg, new[] { "-hide_banner", "-v", "error", "-i", src }.Concat(audioArgs)
        .Concat(["-map", "0:v:0", "-map", "1:a:0", "-c", "copy", "-movflags", "+faststart", "-y", dst]));
    if (r.ExitCode != 0) throw new InvalidOperationException(r.StdErr);
    return dst;
}

async Task<AudioSync.Result> Detect(string file)
{
    var i = (await Ff.ProbeAsync(file))!;
    return await AudioSync.DetectAsync(file, 0, i.Duration, null, CancellationToken.None);
}

string Fmt(AudioSync.Result r) => $"{F(r.AudioLate * 1000, "+0;-0;0")} ms ({r.Confidence} {r.Agreeing}/{r.Consistent})";

int failures = 0;
async Task RunCase(string name, string input, double audioDelay, double expectShiftMs)
{
    Log($"--- case {name}: input={Path.GetFileName(input)}  AudioDelay={F(audioDelay, "+0.###;-0.###")} s  (expected audio shift {F(expectShiftMs, "+0;-0;0")} ms)");
    var info = (await Ff.ProbeAsync(input))!;
    var outFile = Path.Combine(outDir, $"out_{name}.mp4");
    var req = new EncodeRequest(info, 0, null, turbo, new EditSettings(), outFile, AudioDelay: audioDelay);
    Log("ffmpeg args: " + string.Join(" ", Pipeline.Build(req)[0].Select(x => x.Contains(' ') ? $"\"{x}\"" : x)).Replace(outDir, "<out>"));

    var detIn = await Detect(input);
    var sw = Stopwatch.StartNew();
    double lastFrac = 0, lastSpeed = 0; int ticks = 0;
    await Pipeline.EncodeAsync(req, (f, sp, _) => { lastFrac = f; lastSpeed = sp; ticks++; }, CancellationToken.None);
    var enc = sw.Elapsed.TotalSeconds;

    var oi = (await Ff.ProbeAsync(outFile))!;
    var decode = await Ff.RunAsync(Ff.Ffmpeg, ["-hide_banner", "-v", "error", "-xerror", "-i", outFile, "-f", "null", "-"]);
    var detOut = await Detect(outFile);
    var (shift, score) = await AudioShift(input, outFile);

    Log($"  input : {info.Width}x{info.Height} {F(info.Fps, "0.###")} fps {info.VideoCodec}/{info.AudioCodec} {F(info.Duration, "0.000")} s {info.SizeBytes} B");
    Log($"  output: {oi.Width}x{oi.Height} {F(oi.Fps, "0.###")} fps {oi.VideoCodec}/{oi.AudioCodec} {F(oi.Duration, "0.000")} s {oi.SizeBytes} B ({F(100.0 * oi.SizeBytes / info.SizeBytes, "0.0")} % of input)");
    Log($"  streams: {await StreamStart(outFile)}");
    Log($"  encode: {F(enc, "0.00")} s wall ({F(oi.Duration / enc, "0.0")}x realtime; last ffmpeg speed {F(lastSpeed, "0.0")}x, {ticks} progress ticks, final fraction {F(lastFrac, "0.00")})");
    Log($"  decode check: exit {decode.ExitCode} {decode.StdErr.Trim()}");
    Log($"  AudioSync input : {Fmt(detIn)}");
    Log($"  AudioSync output: {Fmt(detOut)}   (delta {F((detOut.AudioLate - detIn.AudioLate) * 1000, "+0;-0;0")} ms)");
    Log($"  waveform xcorr in->out: {F(shift, "+0;-0;0")} ms (corr {F(score, "0.00")})");
    bool ok = decode.ExitCode == 0 && Math.Abs(oi.Duration - info.Duration) < 0.5 && Math.Abs(shift - expectShiftMs) <= 10;
    if (!ok) failures++;
    Log($"  => {(ok ? "PASS" : "FAIL")} (play ok, duration within 0.5 s, audio shift within 10 ms of expected)");
    Log();
}

// ---------------------------------------------------------------- 2. real range
Log("=== REAL RANGE (a Steam session, lossless 30 s) ===");
var realLate = await Inject(rangeFile, Path.Combine(outDir, "real_late300.mp4"), +0.3);
var realEarly = await Inject(rangeFile, Path.Combine(outDir, "real_early300.mp4"), -0.3);
await RunCase("real_delay+300", rangeFile, +0.3, +300);              // the literal task: AudioDelay = 0.3 on the untouched range
await RunCase("real_late300_fix", realLate, -0.3, -300);             // sound injected 300 ms late, fix = AudioDelay -0.3
await RunCase("real_early300_fix", realEarly, +0.3, +300);           // sound injected 300 ms early, fix = AudioDelay +0.3

// ---------------------------------------------------------------- 3. synthetic reference (FastEncoder's own "sound 300 ms late" clip)
Log("=== SYNTHETIC REFERENCE (flash + click, sound 300 ms late) ===");
{
    const double late = 0.3;
    const string flips = "mod(floor(T*1.3)+floor(T*0.77+0.2),2)";
    var clicks = $"lt(mod((t-{late.ToString(CultureInfo.InvariantCulture)})*1.3,1),0.013)+lt(mod((t-{late.ToString(CultureInfo.InvariantCulture)})*0.77+0.2,1),0.008)";
    var syn = Path.Combine(outDir, "synthetic_late300.mp4");
    var gen = await Ff.RunAsync(Ff.Ffmpeg, ["-hide_banner", "-loglevel", "error", "-y",
        "-f", "lavfi", "-i", "color=c=black:s=1280x720:r=60:d=30",
        "-f", "lavfi", "-i", $"aevalsrc='0.8*min(1,{clicks})*sin(2*PI*2500*t)+0.01*(random(0)-0.5)':s=48000:d=30",
        "-vf", $"geq=lum='16+219*{flips}':cb=128:cr=128",
        "-c:v", "libx264", "-preset", "ultrafast", "-crf", "18", "-g", "60", "-c:a", "aac", "-b:a", "128k", "-shortest", syn]);
    if (gen.ExitCode != 0) { Log("FAIL synthetic: " + gen.StdErr); return 1; }
    await RunCase("synthetic_late300_fix", syn, -0.3, -300);
    await RunCase("synthetic_delay+300", syn, +0.3, +300);
}

Log(failures == 0 ? "ALL CASES PASSED" : $"{failures} CASE(S) FAILED");
return failures == 0 ? 0 : 1;
