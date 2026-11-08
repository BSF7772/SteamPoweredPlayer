// SPDX-License-Identifier: GPL-3.0-or-later
using FastEncoder.Core;
using SteamPoweredPlayer.Core.Export;

namespace SteamPoweredPlayer.Tests.Export;

public sealed class ExportLadderTests
{
    private static readonly EncoderInfo Nvenc = Encoders.Get("av1_nvenc");

    private static LadderTier T(string id) => ExportLadder.Tier(id)!;

    private static string SpeedName(EncodeSettings s) => Encoders.Get(s.Encoder).Speeds[s.Speed];

    [Fact]
    public void Five_tiers_from_quality_to_speed_and_the_default_is_medium()
    {
        Assert.Equal(["ultra-quality", "quality", "medium", "performance", "ultra-performance"], ExportLadder.Tiers.Select(t => t.Id));
        Assert.Equal("medium", ExportLadder.DefaultId);
    }

    [Theory]
    [InlineData("ultra-quality", 24, "5")]
    [InlineData("quality", 28, "5")]
    [InlineData("medium", 32, "6")]
    [InlineData("performance", 33, "10")]
    [InlineData("ultra-performance", 35, "12")]
    public void Cpu_mapping_matches_the_approved_table(string id, int crf, string preset)
    {
        var s = ExportLadder.Build(T(id), LadderEngine.Cpu, Nvenc);
        Assert.Equal(("svtav1", crf, preset), (s.Encoder, s.Quality, SpeedName(s)));
    }

    [Theory]
    [InlineData("quality", 33, "p7", GpuProfile.Quality)]
    [InlineData("medium", 36, "p4", GpuProfile.Balanced)]
    [InlineData("performance", 36, "p2", GpuProfile.Fast)]
    [InlineData("ultra-performance", 36, "p1", GpuProfile.Fast)]
    public void Gpu_mapping_matches_the_approved_table(string id, int cq, string preset, GpuProfile profile)
    {
        var s = ExportLadder.Build(T(id), LadderEngine.Gpu, Nvenc);
        Assert.Equal(("av1_nvenc", cq, preset, profile), (s.Encoder, s.Quality, SpeedName(s), s.GpuProfile));
    }

    [Fact]
    public void Ultra_quality_is_cpu_only_even_when_the_gpu_is_asked_for()
    {
        Assert.False(ExportLadder.UsesGpu(T("ultra-quality"), LadderEngine.Gpu, Nvenc));
        Assert.Equal("svtav1", ExportLadder.Build(T("ultra-quality"), LadderEngine.Auto, Nvenc).Encoder);
    }

    [Theory]
    [InlineData(LadderEngine.Auto, true, true)]
    [InlineData(LadderEngine.Gpu, true, true)]
    [InlineData(LadderEngine.Cpu, true, false)]
    [InlineData(LadderEngine.Auto, false, false)]
    [InlineData(LadderEngine.Gpu, false, false)]
    public void Auto_uses_the_gpu_if_present(LadderEngine engine, bool hasGpu, bool usesGpu) =>
        Assert.Equal(usesGpu, ExportLadder.UsesGpu(T("medium"), engine, hasGpu ? Nvenc : null));

    [Theory]
    [InlineData("hevc_nvenc", "p4", 28)]
    [InlineData("h264_nvenc", "p4", 27)]
    [InlineData("av1_qsv", "medium", 36)]
    [InlineData("hevc_amf", "balanced", 28)]
    public void Medium_maps_to_every_vendor(string encoder, string preset, int quality)
    {
        var s = ExportLadder.Build(T("medium"), LadderEngine.Auto, Encoders.Get(encoder));
        Assert.Equal((encoder, preset, quality), (s.Encoder, SpeedName(s), s.Quality));
    }

    [Fact]
    public void Special_presets_exist_in_fastencoder() =>
        Assert.All(ExportLadder.SpecialIds, id => Assert.NotNull(Presets.Get(id)));

    [Theory]
    [InlineData("medium", true)]
    [InlineData("trim", true)]
    [InlineData("balanced", false)]         // an old saved choice falls back to the default
    [InlineData(null, false)]
    public void Saved_choice_is_validated(string? id, bool known) => Assert.Equal(known, ExportLadder.IsKnown(id));
}
