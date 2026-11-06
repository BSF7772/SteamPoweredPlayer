// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S1 part B: timed measurement run (--mode play). ONLY meaningful on an otherwise idle machine; driven by perf/run-perf.ps1.
// Plays the lead EDL at 1x for --seconds (looping by reload at EOF), samples process CPU/RAM/GPU memory every 2 s into
// out/<tag>.csv, then runs --seeks seek-by-reload latencies and writes out/<tag>.json.
using System.Diagnostics;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace S1App;

public static class PerfRun
{
    static string C(double d) => d.ToString("F3", CultureInfo.InvariantCulture);

    public static async Task<int> RunAsync(MainWindow w)
    {
        var o = w.Opt; var m = w.Mpv!; var r = w.Rend!;
        var proc = Process.GetCurrentProcess();
        var csvPath = Path.Combine(o.Out, o.Tag + ".csv");
        var csv = new StringBuilder("t_s,cpu_s,ws_mb,private_mb,gpu_local_mb,gpu_nonlocal_mb,presents,time_pos\n");

        // open -> first frame (first real open after pre-warm), informational on a loaded machine
        var mark = w.Load(w.T0);
        long ev = m.WaitEvent(MpvConst.EvPlaybackRestart, mark, 5000);
        double openMs = ev > 0 ? MpvPlayer.Ms(ev - mark) : double.NaN;
        Log.W($"open->restart {C(openMs)} ms");

        var sw = Stopwatch.StartNew();
        bool reset = false;
        double cpu0 = 0, wall0 = 0, media = 0, lastTp = double.NaN;
        long presents0 = 0;
        double nextSample = 0;
        var samples = new List<(double ws, double priv, double gl, double gn)>();
        while (sw.Elapsed.TotalSeconds < o.Warmup + o.Seconds)
        {
            double now = sw.Elapsed.TotalSeconds;
            if (!reset && now >= o.Warmup)
            {
                reset = true; r.ResetStats(); proc.Refresh();
                cpu0 = proc.TotalProcessorTime.TotalSeconds; wall0 = now; presents0 = r.PresentCount; lastTp = m.GetDouble("time-pos");
            }
            if (m.GetString("eof-reached") == "yes") { w.Load(w.T0); lastTp = double.NaN; }   // loop: reload at EOF is seamless (S2)
            double tp = m.GetDouble("time-pos");
            if (reset && !double.IsNaN(tp) && !double.IsNaN(lastTp) && tp >= lastTp && tp - lastTp < 5) media += tp - lastTp;
            if (!double.IsNaN(tp)) lastTp = tp;
            if (now >= nextSample)
            {
                nextSample = now + 2; proc.Refresh();
                double wsMb = proc.WorkingSet64 / 1048576.0, privMb = proc.PrivateMemorySize64 / 1048576.0, gl = r.GpuLocalMb(), gn = r.GpuNonLocalMb();
                csv.Append(string.Create(CultureInfo.InvariantCulture, $"{now:F1},{proc.TotalProcessorTime.TotalSeconds:F3},{wsMb:F1},{privMb:F1},{gl:F1},{gn:F1},{r.PresentCount},{tp:F3}\n"));
                if (reset) samples.Add((wsMb, privMb, gl, gn));
            }
            await Task.Delay(100);
        }
        proc.Refresh();
        double cpuUsed = proc.TotalProcessorTime.TotalSeconds - cpu0, wall = sw.Elapsed.TotalSeconds - wall0;
        var sum = r.Summary();
        double presentsPerSec = (r.PresentCount - presents0) / Math.Max(0.001, wall);
        File.WriteAllText(csvPath, csv.ToString());

        // seek-by-reload latency (restart event = first frame of the new position is ready)
        var rng = new Random(1);
        var lat = new List<double>(); var landed = new List<double>();
        for (int i = 0; i < o.Seeks; i++)
        {
            double t = w.T0 + 5 + rng.NextDouble() * (w.TEnd - w.T0 - 15);
            var mk = w.Load(t);
            long e = m.WaitEvent(MpvConst.EvPlaybackRestart, mk, 3000);
            lat.Add(e > 0 ? MpvPlayer.Ms(e - mk) : double.NaN);
            await Task.Delay(1200);
            landed.Add(m.GetDouble("time-pos") - t);
        }

        double Med(IEnumerable<double> x) { var s = x.Where(v => !double.IsNaN(v)).Order().ToArray(); return s.Length == 0 ? double.NaN : s[s.Length / 2]; }
        double mediaSeconds = Math.Max(0.001, media);
        var result = new
        {
            tag = o.Tag, hwdec = o.HwDec, warmupS = o.Warmup, wallS = wall, mediaS = media,
            cpuSeconds = cpuUsed,
            cpuPctOfOneCorePerMediaSecond = 100.0 * cpuUsed / mediaSeconds,
            cpuPctOfAllCores = 100.0 * cpuUsed / mediaSeconds / Environment.ProcessorCount,
            logicalCores = Environment.ProcessorCount,
            openToFirstFrameMs = openMs,
            presentsPerSec,
            present = sum,
            wsMbMax = samples.Count > 0 ? samples.Max(s => s.ws) : double.NaN,
            privateMbMax = samples.Count > 0 ? samples.Max(s => s.priv) : double.NaN,
            gpuLocalMbFirst = samples.Count > 0 ? samples[0].gl : double.NaN,
            gpuLocalMbLast = samples.Count > 0 ? samples[^1].gl : double.NaN,
            gpuLocalMbMax = samples.Count > 0 ? samples.Max(s => s.gl) : double.NaN,
            seekLatencyMs = lat, seekLatencyMedianMs = Med(lat), seekLandedMinusTarget = landed,
        };
        File.WriteAllText(Path.Combine(o.Out, o.Tag + ".json"), JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true, NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals }));   // NaN = "not measured"
        return 0;
    }
}
