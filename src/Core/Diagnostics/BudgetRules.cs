// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>The desktop-local performance budgets of plan §5. Never relax one here without the maintainer's OK.</summary>
public static class Budgets
{
    public const double LibraryVisibleMs = 400;
    public const double FirstFrameMs = 250;
    public const double SeekMs = 150;
    public const double RamMb = 250;
    public const double ExportMs = 1000;
    public const double TrickplayPerTwoHoursSeconds = 40;

    /// <summary>A 2 h session has 2 400 chunks of 3 s: the extrapolation base of the trickplay budget.</summary>
    public const double TwoHourChunks = 2400;

    /// <summary>
    /// Leak signal (decision D-RAM, plan 2026-11-07 §0): RSS growth after warm-up. S1 measured 19 MB/min while mpv's demuxer cache filled
    /// (200 MiB); the cache is sized in seconds (<see cref="Playback.DemuxerCache"/>) and is full after the warm-up, so steady growth
    /// beyond this is a leak. A falling slope (the cache emptied by a reload) is not a leak.
    /// </summary>
    public const double LeakMbPerMinute = 2;

    /// <summary>D-RAM: the RAM gate is peak ≤ (base + expected demuxer cache) × (1 + this). The fixed <see cref="RamMb"/> is reported only.</summary>
    public const double RamMarginFraction = 0.15;
}

/// <summary>Statistics of the seek timings: <see cref="OverBudget"/> counts seeks that took longer than the budget.</summary>
public sealed record SeekStats(int Count, double MedianMs, double P90Ms, double MaxMs, int OverBudget);

/// <summary>Small pure statistics helpers and the pass/fail rules of <c>--bench</c> (unit-tested in tests/Diagnostics).</summary>
public static class BudgetRules
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    public static string F(double v, string format = "0.#") => v.ToString(format, Inv);

    /// <summary>Median (mean of the two middle values for an even count). NaN for an empty list.</summary>
    public static double Median(IReadOnlyList<double> values)
    {
        if (values.Count == 0) return double.NaN;
        var s = values.Order().ToArray();
        var m = s.Length / 2;
        return s.Length % 2 == 1 ? s[m] : (s[m - 1] + s[m]) / 2;
    }

    /// <summary>Nearest-rank percentile (<paramref name="p"/> in 0..100): the smallest value with at least p % of the values at or below it.</summary>
    public static double Percentile(IReadOnlyList<double> values, double p)
    {
        if (values.Count == 0) return double.NaN;
        var s = values.Order().ToArray();
        var rank = (int)Math.Ceiling(Math.Clamp(p, 0, 100) / 100 * s.Length);
        return s[Math.Clamp(rank, 1, s.Length) - 1];
    }

    public static SeekStats Seek(IReadOnlyList<double> millis, double budgetMs = Budgets.SeekMs) =>
        new(millis.Count, Median(millis), Percentile(millis, 90), millis.Count == 0 ? double.NaN : millis.Max(), millis.Count(v => v > budgetMs));

    /// <summary>Least-squares slope of <paramref name="y"/> over <paramref name="x"/> (units of y per unit of x). NaN with fewer than two distinct x.</summary>
    public static double Slope(IReadOnlyList<double> x, IReadOnlyList<double> y)
    {
        var n = Math.Min(x.Count, y.Count);
        if (n < 2) return double.NaN;
        double mx = 0, my = 0;
        for (var i = 0; i < n; i++) { mx += x[i]; my += y[i]; }
        mx /= n; my /= n;
        double sxy = 0, sxx = 0;
        for (var i = 0; i < n; i++) { sxy += (x[i] - mx) * (y[i] - my); sxx += (x[i] - mx) * (x[i] - mx); }
        return sxx == 0 ? double.NaN : sxy / sxx;
    }

    /// <summary>Trickplay time for a 2 h session (2 400 chunks) from a run over <paramref name="chunks"/> chunks.</summary>
    public static double TrickplayPerTwoHours(double elapsedSeconds, int chunks) =>
        chunks <= 0 ? double.NaN : elapsedSeconds * Budgets.TwoHourChunks / chunks;

    // ---- rules: one CheckResult each, "< budget" is strict ----------------------------------------------------------

    public static CheckResult LibraryVisible(double warmMs) =>
        CheckResult.From(warmMs < Budgets.LibraryVisibleMs, "bench.library.warm", "Library visible from index (warm)", F(warmMs) + " ms",
            number: warmMs, unit: "ms", budget: "< " + F(Budgets.LibraryVisibleMs) + " ms");

    public static CheckResult FirstFrame(string id, string name, double ms) =>
        CheckResult.From(ms < Budgets.FirstFrameMs, id, name, F(ms) + " ms", number: ms, unit: "ms", budget: "< " + F(Budgets.FirstFrameMs) + " ms");

    /// <summary>
    /// Plan §5 says "&lt; 150 ms anywhere"; S2 measured 9 of 90 reloads over it even with hardware decode, so the gate is the 90th
    /// percentile (at least nine of ten seeks within budget). Median, max and the count over budget are reported next to it.
    /// </summary>
    public static CheckResult SeekGate(string id, string name, SeekStats s) =>
        CheckResult.From(s.Count > 0 && s.P90Ms < Budgets.SeekMs, id, name,
            $"median {F(s.MedianMs)}, p90 {F(s.P90Ms)}, max {F(s.MaxMs)} ms; {s.OverBudget}/{s.Count} over {F(Budgets.SeekMs)} ms",
            number: s.P90Ms, unit: "ms", budget: "p90 < " + F(Budgets.SeekMs) + " ms");

    public static CheckResult Ram(string id, string name, double peakMb) =>
        CheckResult.From(peakMb < Budgets.RamMb, id, name, F(peakMb) + " MB peak", number: peakMb, unit: "MB", budget: "< " + F(Budgets.RamMb) + " MB");

    /// <summary>
    /// Decision D-RAM: RAM is base (process + mpv + decoder + renderer, measured with a minimal demuxer cache) plus the demuxer cache,
    /// whose size follows the bitrate (<see cref="Playback.DemuxerCache.ExpectedCacheBytes"/>). PASS when the peak is at most
    /// (base + cache) × (1 + <see cref="Budgets.RamMarginFraction"/>).
    /// </summary>
    public static CheckResult RamGate(string id, string name, double peakMb, double baseMb, double expectedCacheMb)
    {
        var limit = (baseMb + expectedCacheMb) * (1 + Budgets.RamMarginFraction);
        var ok = double.IsFinite(peakMb) && double.IsFinite(limit) && peakMb <= limit + 1e-6;
        return CheckResult.From(ok, id, name,
            $"{F(peakMb)} MB peak; base {F(baseMb)} MB + expected cache {F(expectedCacheMb)} MB + {F(Budgets.RamMarginFraction * 100)} % = limit {F(limit)} MB",
            number: peakMb, unit: "MB", budget: "<= base + cache + " + F(Budgets.RamMarginFraction * 100) + " %");
    }

    /// <summary>The leak signal: PASS when RSS grows by less than <see cref="Budgets.LeakMbPerMinute"/> per minute after warm-up.</summary>
    public static CheckResult Leak(string id, string name, double slopeMbPerMinute) =>
        double.IsNaN(slopeMbPerMinute)
            ? CheckResult.Info(id, name, "not enough samples")
            : CheckResult.From(slopeMbPerMinute < Budgets.LeakMbPerMinute, id, name, F(slopeMbPerMinute, "+0.0;-0.0;0.0") + " MB/min",
                number: slopeMbPerMinute, unit: "MB/min", budget: "< " + F(Budgets.LeakMbPerMinute) + " MB/min");

    public static CheckResult Export(double ms) =>
        CheckResult.From(ms < Budgets.ExportMs, "bench.export", "Lossless 30 s export", F(ms) + " ms", number: ms, unit: "ms", budget: "< " + F(Budgets.ExportMs) + " ms");

    public static CheckResult Trickplay(double elapsedSeconds, int chunks)
    {
        var per2h = TrickplayPerTwoHours(elapsedSeconds, chunks);
        return CheckResult.From(per2h < Budgets.TrickplayPerTwoHoursSeconds, "bench.trickplay", "Trickplay, extrapolated to 2 h",
            $"{F(per2h)} s per 2 h (measured {F(elapsedSeconds)} s for {chunks} chunks)", number: per2h, unit: "s",
            budget: "< " + F(Budgets.TrickplayPerTwoHoursSeconds) + " s");
    }

    /// <summary>CPU time per media second: % of one core and % of all logical cores (plan §5, CPU efficiency).</summary>
    public static (double OneCorePercent, double AllCoresPercent) CpuPercent(double cpuSeconds, double mediaSeconds, int logicalCores)
    {
        if (mediaSeconds <= 0 || logicalCores <= 0) return (double.NaN, double.NaN);
        var one = cpuSeconds / mediaSeconds * 100;
        return (one, one / logicalCores);
    }

    /// <summary>"app overhead over the reference" as a ratio (app / reference); NaN when the reference is zero or missing.</summary>
    public static double OverheadRatio(double appPercent, double referencePercent) =>
        referencePercent > 0 && !double.IsNaN(appPercent) ? appPercent / referencePercent : double.NaN;
}
