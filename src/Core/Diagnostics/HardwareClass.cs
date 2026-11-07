// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using Microsoft.Win32;
using Vortice.DXGI;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>
/// What a report may say about the machine (plan §6: hardware class only, no machine names): CPU vendor and family tier without
/// the SKU, logical cores, ISA, the GPU's vendor and whether it has dedicated memory, the Windows build class. No adapter name,
/// no user or host name, no serial, no path.
/// </summary>
public sealed record HardwareClass(string CpuClass, int LogicalCores, string Isa, string GpuVendor, string GpuKind, string OsClass)
{
    public static HardwareClass Detect()
    {
        string? name = null;
        try
        {
            using var k = Registry.LocalMachine.OpenSubKey(@"HARDWARE\DESCRIPTION\System\CentralProcessor\0");
            name = k?.GetValue("ProcessorNameString") as string;
        }
        catch (Exception e) when (e is System.Security.SecurityException or UnauthorizedAccessException or IOException) { }
        name ??= Environment.GetEnvironmentVariable("PROCESSOR_IDENTIFIER");

        var (vendor, kind) = DetectGpu();
        return new(ClassifyCpu(name), Environment.ProcessorCount, ClassifyIsa(RuntimeInformation.OSArchitecture), vendor, kind, ClassifyOs(Environment.OSVersion.Version));
    }

    /// <summary>"Intel Core i7-class", "AMD Ryzen 9-class", ...: vendor and tier, never the model number.</summary>
    public static string ClassifyCpu(string? processorName)
    {
        if (string.IsNullOrWhiteSpace(processorName)) return "unknown CPU";
        var n = processorName;
        if (Regex.Match(n, @"Core\(TM\)\s*Ultra\s*([3579])", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) is { Success: true } u)
            return $"Intel Core Ultra {u.Groups[1].Value}-class";
        if (Regex.Match(n, @"Core\(TM\)\s*(i[3579])", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) is { Success: true } c)
            return $"Intel Core {c.Groups[1].Value}-class";
        if (Regex.Match(n, @"Ryzen\s*(?:AI\s*)?([3579])", RegexOptions.IgnoreCase, TimeSpan.FromSeconds(1)) is { Success: true } r)
            return $"AMD Ryzen {r.Groups[1].Value}-class";
        if (n.Contains("Threadripper", StringComparison.OrdinalIgnoreCase)) return "AMD Threadripper-class";
        if (n.Contains("EPYC", StringComparison.OrdinalIgnoreCase)) return "AMD EPYC-class";
        if (n.Contains("Xeon", StringComparison.OrdinalIgnoreCase)) return "Intel Xeon-class";
        if (n.Contains("Snapdragon", StringComparison.OrdinalIgnoreCase)) return "Qualcomm Snapdragon-class";
        if (n.Contains("Intel", StringComparison.OrdinalIgnoreCase)) return "Intel CPU";
        if (n.Contains("AMD", StringComparison.OrdinalIgnoreCase)) return "AMD CPU";
        return "other CPU";
    }

    public static string ClassifyIsa(Architecture a) => a switch
    {
        Architecture.X64 => "x64",
        Architecture.Arm64 => "arm64",
        Architecture.X86 => "x86",
        _ => a.ToString().ToLowerInvariant(),
    };

    /// <summary>"Windows 11" for builds from 22000, "Windows 10" otherwise.</summary>
    public static string ClassifyOs(Version v) => v.Build >= 22000 ? "Windows 11" : "Windows 10";

    /// <summary>NVIDIA, AMD, Intel, Microsoft (software adapter) or other, from the PCI vendor id.</summary>
    public static string ClassifyGpuVendor(int vendorId) => vendorId switch
    {
        0x10DE => "NVIDIA",
        0x1002 or 0x1022 => "AMD",
        0x8086 => "Intel",
        0x1414 => "Microsoft (software)",
        _ => "other",
    };

    /// <summary>"discrete" with at least 1 GiB of dedicated video memory, "integrated" otherwise.</summary>
    public static string ClassifyGpuKind(ulong dedicatedVideoMemoryBytes) => dedicatedVideoMemoryBytes >= 1UL << 30 ? "discrete" : "integrated";

    private static (string Vendor, string Kind) DetectGpu()
    {
        try
        {
            if (DXGI.CreateDXGIFactory1<IDXGIFactory1>(out var factory).Failure || factory is null) return ("unknown", "unknown");
            using (factory)
            {
                if (factory.EnumAdapters1(0, out var adapter).Failure || adapter is null) return ("unknown", "unknown");
                using var _ = adapter;
                var d = adapter.Description1;
                return (ClassifyGpuVendor((int)d.VendorId), ClassifyGpuKind((ulong)d.DedicatedVideoMemory));
            }
        }
        catch (Exception e) when (e is COMException or DllNotFoundException or InvalidOperationException or ArgumentException) { return ("unknown", "unknown"); }
    }

    /// <summary>One line for a report header.</summary>
    public string Describe() =>
        string.Create(CultureInfo.InvariantCulture, $"{CpuClass}, {LogicalCores} logical cores, {Isa}; GPU {GpuVendor} ({GpuKind}); {OsClass}");
}
