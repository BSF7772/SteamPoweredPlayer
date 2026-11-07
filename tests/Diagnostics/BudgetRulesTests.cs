// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Diagnostics;

namespace SteamPoweredPlayer.Tests.Diagnostics;

public sealed class BudgetRulesTests
{
    [Fact]
    public void The_budgets_are_the_ones_of_plan_section_5()
    {
        Assert.Equal(400, Budgets.LibraryVisibleMs);
        Assert.Equal(250, Budgets.FirstFrameMs);
        Assert.Equal(150, Budgets.SeekMs);
        Assert.Equal(250, Budgets.RamMb);
        Assert.Equal(1000, Budgets.ExportMs);
        Assert.Equal(40, Budgets.TrickplayPerTwoHoursSeconds);
        Assert.Equal(2400, Budgets.TwoHourChunks);
    }

    [Theory]
    [InlineData(new double[] { 5 }, 5)]
    [InlineData(new double[] { 1, 3, 2 }, 2)]
    [InlineData(new double[] { 1, 2, 3, 4 }, 2.5)]
    [InlineData(new double[] { 10, 10, 10, 1000 }, 10)]
    public void Median(double[] values, double expected) => Assert.Equal(expected, BudgetRules.Median(values));

    [Fact]
    public void Median_and_percentile_of_nothing_are_NaN()
    {
        Assert.True(double.IsNaN(BudgetRules.Median([])));
        Assert.True(double.IsNaN(BudgetRules.Percentile([], 90)));
    }

    [Fact]
    public void Percentile_is_nearest_rank()
    {
        double[] v = [.. Enumerable.Range(1, 10).Select(i => (double)i)];
        Assert.Equal(9, BudgetRules.Percentile(v, 90));
        Assert.Equal(10, BudgetRules.Percentile(v, 91));
        Assert.Equal(1, BudgetRules.Percentile(v, 0));
        Assert.Equal(10, BudgetRules.Percentile(v, 100));
        Assert.Equal(10, BudgetRules.Percentile(v, 250));        // clamped
        Assert.Equal(7, BudgetRules.Percentile([7], 90));
    }

    [Fact]
    public void Seek_stats_count_what_is_over_the_budget_strictly()
    {
        var s = BudgetRules.Seek([100, 120, 149.9, 150, 151, 400]);
        Assert.Equal(6, s.Count);
        Assert.Equal(2, s.OverBudget);                   // 150 itself is not over
        Assert.Equal(400, s.MaxMs);
        Assert.Equal(149.95, s.MedianMs, 6);
        Assert.Equal(400, s.P90Ms);
    }

    [Fact]
    public void Seek_gate_passes_with_nine_of_ten_inside_the_budget_and_reports_the_rest()
    {
        double[] times = [90, 95, 96, 97, 98, 99, 100, 101, 102, 140];
        var pass = BudgetRules.SeekGate("id", "Seek", BudgetRules.Seek(times));
        Assert.Equal(CheckStatus.Pass, pass.Status);
        Assert.Contains("0/10 over 150 ms", pass.Value);

        double[] slow = [90, 95, 96, 97, 98, 99, 100, 101, 400, 500];       // p90 = 400
        var fail = BudgetRules.SeekGate("id", "Seek", BudgetRules.Seek(slow));
        Assert.Equal(CheckStatus.Fail, fail.Status);
        Assert.Contains("2/10 over 150 ms", fail.Value);
        Assert.Contains("max 500", fail.Value);
        Assert.Equal("p90 < 150 ms", fail.Budget);
    }

    [Fact]
    public void Seek_gate_without_samples_fails()
    {
        Assert.Equal(CheckStatus.Fail, BudgetRules.SeekGate("id", "Seek", BudgetRules.Seek([])).Status);
    }

    [Theory]
    [InlineData(399.9, CheckStatus.Pass)]
    [InlineData(400, CheckStatus.Fail)]
    [InlineData(1200, CheckStatus.Fail)]
    public void Library_visible_budget_is_strict(double ms, CheckStatus expected) => Assert.Equal(expected, BudgetRules.LibraryVisible(ms).Status);

    [Theory]
    [InlineData(95, CheckStatus.Pass)]
    [InlineData(250, CheckStatus.Fail)]
    public void First_frame_budget_is_strict(double ms, CheckStatus expected) => Assert.Equal(expected, BudgetRules.FirstFrame("i", "n", ms).Status);

    [Theory]
    [InlineData(249, CheckStatus.Pass)]
    [InlineData(295, CheckStatus.Fail)]
    public void Ram_budget_is_strict(double mb, CheckStatus expected) => Assert.Equal(expected, BudgetRules.Ram("i", "n", mb).Status);

    [Theory]
    [InlineData(999, CheckStatus.Pass)]
    [InlineData(1000, CheckStatus.Fail)]
    public void Export_budget_is_strict(double ms, CheckStatus expected) => Assert.Equal(expected, BudgetRules.Export(ms).Status);

    [Fact]
    public void Trickplay_is_extrapolated_to_a_two_hour_session()
    {
        Assert.Equal(60, BudgetRules.TrickplayPerTwoHours(10, 400), 9);           // 10 s for 400 chunks -> 60 s for 2400
        Assert.Equal(CheckStatus.Fail, BudgetRules.Trickplay(10, 400).Status);
        var ok = BudgetRules.Trickplay(5.6, 2400);                                 // S4: about 5.6 s per 2 h
        Assert.Equal(CheckStatus.Pass, ok.Status);
        Assert.Contains("5.6 s per 2 h", ok.Value);
        Assert.True(double.IsNaN(BudgetRules.TrickplayPerTwoHours(1, 0)));
    }

    [Fact]
    public void Slope_is_the_least_squares_line()
    {
        Assert.Equal(2, BudgetRules.Slope([0, 1, 2, 3], [5, 7, 9, 11]), 9);
        Assert.Equal(-3, BudgetRules.Slope([0, 2, 4], [10, 4, -2]), 9);
        Assert.Equal(0, BudgetRules.Slope([0, 1, 2], [4, 4, 4]), 9);
        Assert.True(double.IsNaN(BudgetRules.Slope([1], [1])));
        Assert.True(double.IsNaN(BudgetRules.Slope([1, 1], [1, 2])));            // no spread in x
    }

    [Fact]
    public void Leak_signal_passes_below_the_slope_limit_and_is_informational_without_samples()
    {
        Assert.Equal(CheckStatus.Pass, BudgetRules.Leak("i", "n", 0.5).Status);
        Assert.Equal(CheckStatus.Pass, BudgetRules.Leak("i", "n", -8).Status);
        Assert.Equal(CheckStatus.Fail, BudgetRules.Leak("i", "n", Budgets.LeakMbPerMinute).Status);
        Assert.Equal(CheckStatus.Fail, BudgetRules.Leak("i", "n", 19).Status);                    // S1: 19 MB/min while the cache filled
        Assert.Equal(CheckStatus.Info, BudgetRules.Leak("i", "n", double.NaN).Status);
        Assert.Equal("+0.5 MB/min", BudgetRules.Leak("i", "n", 0.5).Value);
    }

    [Fact]
    public void Cpu_percent_is_per_media_second_of_one_core_and_of_all_cores()
    {
        var (one, all) = BudgetRules.CpuPercent(1.56, 30, 16);          // S2: mpv CLI d3d11va, 5.2 % of one core = 0.32 % of 16
        Assert.Equal(5.2, one, 9);
        Assert.Equal(0.325, all, 9);
        var (n1, n2) = BudgetRules.CpuPercent(1, 0, 16);
        Assert.True(double.IsNaN(n1) && double.IsNaN(n2));
        Assert.True(double.IsNaN(BudgetRules.CpuPercent(1, 1, 0).AllCoresPercent));
    }

    [Fact]
    public void Overhead_ratio_compares_with_the_reference_run()
    {
        Assert.Equal(5.6, BudgetRules.OverheadRatio(29.12, 5.2), 9);
        Assert.True(double.IsNaN(BudgetRules.OverheadRatio(1, 0)));
        Assert.True(double.IsNaN(BudgetRules.OverheadRatio(double.NaN, 5)));
    }

    [Fact]
    public void Rule_results_carry_numbers_and_budgets_for_the_json()
    {
        var c = BudgetRules.LibraryVisible(120);
        Assert.Equal(120, c.Number);
        Assert.Equal("ms", c.Unit);
        Assert.Equal("< 400 ms", c.Budget);
        Assert.Equal("120 ms", c.Value);
    }
}
