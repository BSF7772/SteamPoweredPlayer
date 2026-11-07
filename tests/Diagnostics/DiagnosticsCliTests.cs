// SPDX-License-Identifier: GPL-3.0-or-later
using System.Runtime.InteropServices;
using SteamPoweredPlayer.Core.Diagnostics;

namespace SteamPoweredPlayer.Tests.Diagnostics;

public sealed class DiagnosticsCliTests
{
    [Fact]
    public void No_diagnostics_flag_means_the_app_starts_normally()
    {
        Assert.Null(DiagnosticsCli.TryParse([]));
        Assert.Null(DiagnosticsCli.TryParse(["--sample", "--theme", "dark"]));
    }

    [Fact]
    public void Selftest_takes_the_report_path()
    {
        var r = DiagnosticsCli.TryParse(["--selftest", @"C:\out\selftest.txt"])!;
        Assert.Equal(DiagnosticsCommand.SelfTest, r.Command);
        Assert.Equal(@"C:\out\selftest.txt", r.ReportPath);
        Assert.False(r.Quick);
        Assert.Null(r.Library);
    }

    [Fact]
    public void Bench_takes_a_path_a_library_and_quick_in_any_order()
    {
        var r = DiagnosticsCli.TryParse(["--quick", "--bench", "b.txt", "--library", @"D:\rec"])!;
        Assert.Equal(DiagnosticsCommand.Bench, r.Command);
        Assert.Equal("b.txt", r.ReportPath);
        Assert.Equal(@"D:\rec", r.Library);
        Assert.True(r.Quick);
    }

    [Fact]
    public void A_missing_path_falls_back_to_a_file_in_the_temp_directory()
    {
        var r = DiagnosticsCli.TryParse(["--bench", "--quick"])!;
        Assert.True(r.Quick);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "SteamPoweredPlayer-bench.txt"), r.ReportPath);
        Assert.Equal(Path.Combine(Path.GetTempPath(), "SteamPoweredPlayer-selftest.txt"), DiagnosticsCli.TryParse(["--selftest"])!.ReportPath);
    }

    [Fact]
    public void Flags_are_case_insensitive_and_a_library_flag_without_a_value_is_ignored()
    {
        var r = DiagnosticsCli.TryParse(["--BENCH", "x.txt", "--Library"])!;
        Assert.Equal(DiagnosticsCommand.Bench, r.Command);
        Assert.Null(r.Library);
        Assert.Null(DiagnosticsCli.TryParse(["--bench", "x.txt", "--library", "--quick"])!.Library);
    }

    [Fact]
    public void The_first_command_on_the_line_wins()
    {
        Assert.Equal(DiagnosticsCommand.SelfTest, DiagnosticsCli.TryParse(["--selftest", "a.txt", "--bench", "b.txt"])!.Command);
    }
}

public sealed class RedactorTests
{
    [Fact]
    public void Paths_are_replaced()
    {
        var s = Redactor.Scrub(@"could not open C:\Users\someone\Videos\clip.mp4 and D:/Steam/rec/x and \\nas\share\v\a.m4s today");
        Assert.DoesNotContain("Videos", s);
        Assert.DoesNotContain("Steam", s);
        Assert.DoesNotContain("nas", s);
        Assert.Contains("<path>", s);
        Assert.StartsWith("could not open ", s);
        Assert.EndsWith(" today", s);
    }

    [Fact]
    public void Given_secrets_and_the_machine_and_user_names_are_replaced_case_insensitively()
    {
        var s = Redactor.Scrub("game Counter-Strike on " + Environment.MachineName + " by " + Environment.UserName, "counter-strike");
        Assert.DoesNotContain("Counter", s, StringComparison.OrdinalIgnoreCase);
        if (Environment.MachineName.Length >= 3) Assert.DoesNotContain(Environment.MachineName, s, StringComparison.OrdinalIgnoreCase);
        if (Environment.UserName.Length >= 3) Assert.DoesNotContain(Environment.UserName, s, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Newlines_collapse_and_null_is_empty()
    {
        Assert.Equal("a b", Redactor.Scrub("a\r\nb"));
        Assert.Equal("", Redactor.Scrub(null));
    }

    [Fact]
    public void An_exception_is_its_type_and_a_clean_message()
    {
        var s = Redactor.Describe(new FileNotFoundException(@"missing C:\secret\folder\file.m4s"));
        Assert.StartsWith("FileNotFoundException: missing ", s);
        Assert.DoesNotContain("secret", s);
    }
}

public sealed class HardwareClassTests
{
    [Theory]
    [InlineData("13th Gen Intel(R) Core(TM) i7-13700K", "Intel Core i7-class")]
    [InlineData("Intel(R) Core(TM) Ultra 7 155H", "Intel Core Ultra 7-class")]
    [InlineData("AMD Ryzen 7 5800X3D 8-Core Processor", "AMD Ryzen 7-class")]
    [InlineData("AMD Ryzen AI 9 HX 370", "AMD Ryzen 9-class")]
    [InlineData("AMD Ryzen Threadripper PRO 5995WX", "AMD Threadripper-class")]
    [InlineData("Intel(R) Xeon(R) W-2295", "Intel Xeon-class")]
    [InlineData("Intel64 Family 6 Model 183 Stepping 1, GenuineIntel", "Intel CPU")]
    [InlineData("Snapdragon X Elite", "Qualcomm Snapdragon-class")]
    [InlineData("Some Chip", "other CPU")]
    [InlineData("", "unknown CPU")]
    [InlineData(null, "unknown CPU")]
    public void The_cpu_is_named_by_vendor_and_tier_never_by_model_number(string? name, string expected)
    {
        var c = HardwareClass.ClassifyCpu(name);
        Assert.Equal(expected, c);
        Assert.DoesNotMatch(@"\d{4}", c);                  // no SKU digits
    }

    [Theory]
    [InlineData(0x10DE, "NVIDIA")]
    [InlineData(0x1002, "AMD")]
    [InlineData(0x8086, "Intel")]
    [InlineData(0x1414, "Microsoft (software)")]
    [InlineData(0x1234, "other")]
    public void The_gpu_vendor_comes_from_the_pci_id(int id, string expected) => Assert.Equal(expected, HardwareClass.ClassifyGpuVendor(id));

    [Fact]
    public void Gpu_kind_and_os_and_isa_classes()
    {
        Assert.Equal("discrete", HardwareClass.ClassifyGpuKind(8UL << 30));
        Assert.Equal("integrated", HardwareClass.ClassifyGpuKind(512UL << 20));
        Assert.Equal("Windows 11", HardwareClass.ClassifyOs(new Version(10, 0, 26200)));
        Assert.Equal("Windows 10", HardwareClass.ClassifyOs(new Version(10, 0, 19045)));
        Assert.Equal("x64", HardwareClass.ClassifyIsa(Architecture.X64));
        Assert.Equal("arm64", HardwareClass.ClassifyIsa(Architecture.Arm64));
    }

    [Fact]
    public void The_description_has_no_names_and_the_detected_class_is_plausible()
    {
        var h = HardwareClass.Detect();
        Assert.True(h.LogicalCores >= 1);
        var text = h.Describe();
        Assert.Contains("logical cores", text);
        Assert.DoesNotContain(Environment.MachineName, text, StringComparison.OrdinalIgnoreCase);
    }
}
