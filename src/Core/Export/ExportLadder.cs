// SPDX-License-Identifier: GPL-3.0-or-later
using FastEncoder.Core;

namespace SteamPoweredPlayer.Core.Export;

/// <summary>The engine switch: Auto uses the GPU when there is one and the tier allows it.</summary>
public enum LadderEngine { Auto, Cpu, Gpu }

/// <param name="CpuOnly">GPU encoders can't match this tier per byte.</param>
public sealed record LadderTier(string Id, string Name, string Tagline, bool CpuOnly, string CpuInfo, string? GpuInfo);

/// <summary>
/// The maintainer-approved export ladder (decision of 2026-11-06, mockup "approved as is"): five tiers from quality to speed, an engine
/// switch, and FastEncoder's special presets beside it. The default is the tier with the best performance per quality: Medium on the
/// GPU costs 0.09 VMAF against the slowest GPU tier and runs 1.9× faster, while the next tier up in speed gains 1.2× and costs 0.36.
/// It builds FastEncoder's <see cref="EncodeSettings"/> from the outside and leaves FastEncoder's own presets alone.
/// </summary>
public static class ExportLadder
{
    public const string DefaultId = "medium";

    /// <summary>FastEncoder preset ids shown as "Special purpose".</summary>
    public static readonly IReadOnlyList<string> SpecialIds = ["archive", "compact", "universal", "target", "trim"];

    public static readonly IReadOnlyList<LadderTier> Tiers =
    [
        new("ultra-quality", "Ultra quality", "Visually lossless", true,
            "SVT-AV1 crf 24, preset 5. Slowest, for footage you care about.", null),
        new("quality", "Quality", "Archival look", false,
            "SVT-AV1 crf 28, preset 5.", "NVENC p7 two-pass. About 2× realtime, same look as Medium for twice the time."),
        new("medium", "Medium", "Best ratio", false,
            "SVT-AV1 crf 32, preset 6.", "NVENC p4, lookahead 8. About 4× realtime, within 0.1 VMAF of the slowest GPU tier."),
        new("performance", "Performance", "Long clips", false,
            "SVT-AV1 crf 33, preset 10.", "NVENC p2. About 5× realtime, 0.4 VMAF below Medium."),
        new("ultra-performance", "Ultra performance", "Fastest", false,
            "SVT-AV1 crf 35, preset 12.", "NVENC p1. Often no faster than Performance, because the encoder chip is the limit."),
    ];

    public static LadderTier? Tier(string? id) => Tiers.FirstOrDefault(t => t.Id == id);

    /// <summary>True for a tier or special preset id this build knows.</summary>
    public static bool IsKnown(string? id) => Tier(id) is not null || (id is not null && SpecialIds.Contains(id));

    /// <summary>What actually encodes: never the GPU for a CPU-only tier or when no GPU encoder passed its test encode.</summary>
    public static bool UsesGpu(LadderTier tier, LadderEngine engine, EncoderInfo? gpu) =>
        !tier.CpuOnly && gpu is not null && engine != LadderEngine.Cpu;

    /// <summary>Settings for a tier. <paramref name="gpu"/> is <see cref="Encoders.BestGpu"/>, passed in so this stays pure.</summary>
    public static EncodeSettings Build(LadderTier tier, LadderEngine engine, EncoderInfo? gpu)
    {
        if (UsesGpu(tier, engine, gpu)) return BuildGpu(tier, gpu!);
        return tier.Id switch
        {
            "ultra-quality" => Svt(24, "5", 160),
            "quality" => Svt(28, "5", 128),
            "performance" => Svt(33, "10", 96),
            "ultra-performance" => Svt(35, "12", 96),
            _ => Svt(32, "6", 96),
        };
    }

    private static EncodeSettings Svt(int crf, string preset, int audioKbps) => new()
    {
        Encoder = "svtav1", Quality = crf, Speed = Array.IndexOf(Encoders.Get("svtav1").Speeds, preset), Audio = "opus", AudioKbps = audioKbps,
    };

    private static EncodeSettings BuildGpu(LadderTier tier, EncoderInfo hw)
    {
        // NVENC p-level, QuickSync preset, AMF quality mode; the same vendor mapping as FastEncoder's GPU presets.
        var (nvenc, qsv, amf, profile) = tier.Id switch
        {
            "quality" => ("p7", "veryslow", "quality", GpuProfile.Quality),
            "performance" => ("p2", "veryfast", "speed", GpuProfile.Fast),
            "ultra-performance" => ("p1", "veryfast", "speed", GpuProfile.Fast),
            _ => ("p4", "medium", "balanced", GpuProfile.Balanced),
        };
        var name = hw.Id.EndsWith("_nvenc", StringComparison.Ordinal) ? nvenc : hw.Id.EndsWith("_qsv", StringComparison.Ordinal) ? qsv : amf;
        var better = tier.Id == "quality" ? 1 : 0;
        var quality = hw.Family switch { Family.AV1 => 36 - 3 * better, Family.HEVC => 28 - 2 * better, _ => 27 - 2 * better };
        return new EncodeSettings
        {
            Encoder = hw.Id, Quality = quality, Speed = Math.Max(0, Array.IndexOf(hw.Speeds, name)),
            Audio = "opus", AudioKbps = tier.Id == "quality" ? 128 : 96, GpuProfile = profile,
        };
    }
}
