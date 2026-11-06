// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S1: command line + tiny file logger (WinExe has no console).
using System.Globalization;
using System.Text;

namespace S1App;

/// <summary>
/// --mode interactive|selftest|play   --fixture &lt;session folder&gt;   --out &lt;dir&gt;   --tag &lt;name&gt;   --hwdec 0|1
/// --first/--last &lt;chunk&gt;   --seconds N (play)   --warmup N (play)   --seeks N (play)
/// </summary>
public sealed record Options(string Mode, string Fixture, string Out, string Tag, bool HwDec, int? First, int? Last,
                             int Seconds, int Warmup, int Seeks)
{
    public static Options Parse(string[] a)
    {
        string Get(string k, string d) { var i = Array.IndexOf(a, k); return i >= 0 && i + 1 < a.Length ? a[i + 1] : d; }
        int? GetI(string k) => int.TryParse(Get(k, ""), NumberStyles.Integer, CultureInfo.InvariantCulture, out var v) ? v : null;
        return new(Get("--mode", "interactive"), Get("--fixture", ""), Get("--out", "out").Replace('\\', '/'), Get("--tag", "run"),
                   Get("--hwdec", "1") != "0", GetI("--first"), GetI("--last"),
                   GetI("--seconds") ?? 60, GetI("--warmup") ?? 8, GetI("--seeks") ?? 0);
    }
}

public static class Log
{
    static string? _path;
    static readonly object L = new();
    static readonly long T0 = System.Diagnostics.Stopwatch.GetTimestamp();

    public static void Init(string path) { _path = path; Directory.CreateDirectory(Path.GetDirectoryName(path)!); }

    public static void W(string msg)
    {
        if (_path is null) return;
        var t = (System.Diagnostics.Stopwatch.GetTimestamp() - T0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        lock (L) File.AppendAllText(_path, string.Create(CultureInfo.InvariantCulture, $"[{t,9:F1} ms] {msg}\n"), Encoding.UTF8);
    }
}
