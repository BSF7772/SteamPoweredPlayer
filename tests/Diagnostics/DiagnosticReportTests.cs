// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using SteamPoweredPlayer.Core.Diagnostics;

namespace SteamPoweredPlayer.Tests.Diagnostics;

public sealed class DiagnosticReportTests
{
    private static DiagnosticReport Sample()
    {
        var r = new DiagnosticReport("selftest", "Title line");
        r.AddHeader("Hardware: test class");
        r.Add(CheckResult.Pass("a.one", "First check", "12.5 ms", number: 12.5, unit: "ms", budget: "< 150 ms"));
        r.Add(CheckResult.Fail("a.two", "Second check", "999 ms", "too slow"));
        r.Add(CheckResult.Skip("later.x", "Presets", "Phase 3/6"));
        r.Add(CheckResult.Info("a.info", "Reference", "5 %"));
        return r;
    }

    [Fact]
    public void Counts_and_the_exit_code_follow_the_failures()
    {
        var r = Sample();
        Assert.Equal((1, 1, 1, 1), (r.Count(CheckStatus.Pass), r.Count(CheckStatus.Fail), r.Count(CheckStatus.Skip), r.Count(CheckStatus.Info)));
        Assert.Equal(1, r.ExitCode);
        Assert.Equal("1 passed, 1 failed, 1 skipped, 1 info", r.Summary());
    }

    [Fact]
    public void Skips_and_infos_never_fail_a_run()
    {
        var r = new DiagnosticReport("bench", "t");
        r.Add(CheckResult.Pass("p", "p"));
        r.Add(CheckResult.Skip("s", "s", "Phase 3/6"));
        r.Add(CheckResult.Info("i", "i", "1"));
        Assert.Equal(0, r.ExitCode);
        Assert.Contains("Result: PASS (exit code 0)", r.ToText());
    }

    [Fact]
    public void An_empty_report_passes_and_an_aborted_one_fails()
    {
        var r = new DiagnosticReport("bench", "t");
        Assert.Equal(0, r.ExitCode);
        r.Abort("no library");
        Assert.Equal(1, r.ExitCode);
        Assert.EndsWith("ABORTED", r.Summary());
        Assert.Contains("ABORTED: no library", r.ToText());
    }

    [Fact]
    public void Text_has_a_label_per_status_with_the_value_and_the_budget()
    {
        var lines = Sample().ToText().Split('\n');
        Assert.Equal("Title line", lines[0]);
        Assert.Equal("Hardware: test class", lines[1]);
        Assert.Contains(lines, l => l.StartsWith("PASS  First check", StringComparison.Ordinal) && l.Contains("12.5 ms") && l.Contains("(budget < 150 ms)"));
        Assert.Contains(lines, l => l.StartsWith("FAIL  Second check", StringComparison.Ordinal) && l.Contains("999 ms") && l.EndsWith("- too slow", StringComparison.Ordinal));
        Assert.Contains(lines, l => l.StartsWith("SKIP (Phase 3/6)", StringComparison.Ordinal) && l.Contains("Presets"));
        Assert.Contains(lines, l => l.StartsWith("INFO  Reference", StringComparison.Ordinal));
        Assert.Contains("Summary: 1 passed, 1 failed, 1 skipped, 1 info", lines);
        Assert.Contains("Result: FAIL (exit code 1)", lines);
    }

    [Fact]
    public void Skip_reason_is_printed_once_in_the_label_and_not_again_after_the_name()
    {
        var line = Sample().ToText().Split('\n').Single(l => l.StartsWith("SKIP", StringComparison.Ordinal));
        Assert.Equal(1, line.Split("Phase 3/6").Length - 1);
        Assert.Equal("SKIP (Phase 3/6)", DiagnosticReport.Label(CheckResult.Skip("x", "x", "Phase 3/6")));
        Assert.Equal("SKIP", DiagnosticReport.Label(new CheckResult("x", "x", CheckStatus.Skip)));
    }

    [Fact]
    public void Text_is_byte_stable_for_the_same_content()
    {
        Assert.Equal(Sample().ToText(), Sample().ToText());
        Assert.Equal(Sample().ToJson(), Sample().ToJson());
        Assert.DoesNotContain('\r', Sample().ToText());
    }

    [Fact]
    public void Json_carries_the_numbers_statuses_and_summary()
    {
        using var doc = JsonDocument.Parse(Sample().ToJson());
        var root = doc.RootElement;
        Assert.Equal("selftest", root.GetProperty("kind").GetString());
        Assert.Equal(1, root.GetProperty("exitCode").GetInt32());
        Assert.Equal(1, root.GetProperty("summary").GetProperty("failed").GetInt32());
        var first = root.GetProperty("checks")[0];
        Assert.Equal("a.one", first.GetProperty("id").GetString());
        Assert.Equal("pass", first.GetProperty("status").GetString());
        Assert.Equal(12.5, first.GetProperty("number").GetDouble());
        Assert.Equal("ms", first.GetProperty("unit").GetString());
        Assert.Equal("< 150 ms", first.GetProperty("budget").GetString());
        Assert.Equal("skip", root.GetProperty("checks")[2].GetProperty("status").GetString());
        Assert.Equal("Phase 3/6", root.GetProperty("checks")[2].GetProperty("note").GetString());
    }

    [Fact]
    public void Json_leaves_out_numbers_that_are_not_finite()
    {
        var r = new DiagnosticReport("bench", "t");
        r.Add(CheckResult.Info("n", "n", "nan", number: double.NaN));
        using var doc = JsonDocument.Parse(r.ToJson());
        Assert.False(doc.RootElement.GetProperty("checks")[0].TryGetProperty("number", out _));
    }

    [Fact]
    public void Numbers_are_written_with_the_invariant_culture()
    {
        var old = Thread.CurrentThread.CurrentCulture;
        try
        {
            // InvariantGlobalization is on, so build a comma-decimal culture by hand
            var comma = (System.Globalization.CultureInfo)System.Globalization.CultureInfo.InvariantCulture.Clone();
            comma.NumberFormat.NumberDecimalSeparator = ",";
            Thread.CurrentThread.CurrentCulture = comma;
            Assert.Equal("12,5", 12.5.ToString(System.Globalization.CultureInfo.CurrentCulture));
            Assert.Equal("12.5", BudgetRules.F(12.5));
            var r = new DiagnosticReport("bench", "t");
            r.Add(CheckResult.Pass("n", "n", number: 0.5));
            Assert.Contains("\"number\": 0.5", System.Text.Encoding.UTF8.GetString(r.ToJson()));
            Assert.Contains("exit code 0", r.ToText());
        }
        finally { Thread.CurrentThread.CurrentCulture = old; }
    }

    [Fact]
    public void WriteTo_writes_the_text_and_a_json_beside_it()
    {
        using var t = new Library.TempDir();
        var path = Path.Combine(t.Sub("out"), "report.txt");
        Sample().WriteTo(path);
        Assert.StartsWith("Title line", File.ReadAllText(path));
        using var doc = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(t.Sub("out"), "report.json")));
        Assert.Equal(4, doc.RootElement.GetProperty("checks").GetArrayLength());
    }

    [Fact]
    public void A_json_report_path_does_not_overwrite_itself()
    {
        Assert.Equal(@"C:\x\r.json.json", DiagnosticReport.JsonPathFor(@"C:\x\r.json"));
        Assert.Equal(@"C:\x\r.json", DiagnosticReport.JsonPathFor(@"C:\x\r.txt"));
        Assert.Equal(@"C:\x\r.json", DiagnosticReport.JsonPathFor(@"C:\x\r"));
    }

    [Fact]
    public void CheckResult_From_maps_a_condition_to_pass_or_fail()
    {
        Assert.Equal(CheckStatus.Pass, CheckResult.From(true, "i", "n").Status);
        Assert.Equal(CheckStatus.Fail, CheckResult.From(false, "i", "n").Status);
    }
}
