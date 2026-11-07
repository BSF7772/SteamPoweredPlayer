// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Tests.Steam;

public sealed class SteamConfigTests
{
    private const string Sample = """
        "UserLocalConfigStore"
        {
            "Software" { "Valve" { "Steam" { "Apps" { "730" { "LastPlayed" "1" } } } } }
            "GameRecording"
            {
                "BackgroundRecordMode"      "1"
                "backgroundmaxkeep"         "120min"
                "BackgroundRecordPath"      "D:\\TestRecordings"
                "InstantClipDuration"       "60"
                "ExportDirectory"           ""
                "PerGameSettings"
                {
                    "730"   { "enabled" "0" }
                    "440"   { "enabled" "1" }
                }
            }
            "friends" { "x" "y" }
        }
        """;

    [Fact]
    public void Reads_all_known_keys()
    {
        var c = SteamConfig.Parse(Sample);
        Assert.Equal(1, c.BackgroundRecordMode);
        Assert.Equal(TimeSpan.FromHours(2), c.BackgroundMaxKeep);
        Assert.Equal(@"D:\TestRecordings", c.BackgroundRecordPath);
        Assert.Equal(TimeSpan.FromSeconds(60), c.InstantClipDuration);
        Assert.Null(c.ExportDirectory);
        Assert.False(c.PerGameEnabled[730]);
        Assert.True(c.PerGameEnabled[440]);
        Assert.Empty(c.Diagnostics);
    }

    [Fact]
    public void Missing_block_gives_nulls()
    {
        var c = SteamConfig.Parse("\"UserLocalConfigStore\" { \"a\" \"b\" }");
        Assert.Null(c.BackgroundRecordMode);
        Assert.Null(c.BackgroundMaxKeep);
        Assert.Null(c.BackgroundRecordPath);
        Assert.Empty(c.PerGameEnabled);
        Assert.Empty(SteamConfig.Parse("").Diagnostics);
    }

    [Fact]
    public void Missing_keys_give_nulls_without_diagnostics()
    {
        var c = SteamConfig.Parse("\"x\" { \"GameRecording\" { \"BackgroundRecordMode\" \"0\" } }");
        Assert.Equal(0, c.BackgroundRecordMode);
        Assert.Null(c.BackgroundMaxKeep);
        Assert.Empty(c.Diagnostics);
    }

    [Theory]
    [InlineData("120min", 7200.0)]
    [InlineData("24h", 86400.0)]
    [InlineData("90s", 90.0)]
    [InlineData("30MIN", 1800.0)]
    public void Known_duration_forms_parse(string text, double seconds) =>
        Assert.Equal(TimeSpan.FromSeconds(seconds), SteamConfig.ParseDuration(text));

    [Theory]
    [InlineData("120")]
    [InlineData("2 days")]
    [InlineData("-5min")]
    [InlineData("")]
    [InlineData("1.5h")]
    public void Unknown_duration_forms_are_null(string text) => Assert.Null(SteamConfig.ParseDuration(text));

    [Fact]
    public void Unknown_values_are_reported_not_guessed()
    {
        var c = SteamConfig.Parse("\"x\" { \"GameRecording\" { \"BackgroundMaxKeep\" \"forever\" \"BackgroundRecordMode\" \"on\" " +
                                  "\"PerGameSettings\" { \"7\" { \"enabled\" \"maybe\" } } } }");
        Assert.Null(c.BackgroundMaxKeep);
        Assert.Null(c.BackgroundRecordMode);
        Assert.Empty(c.PerGameEnabled);
        Assert.Equal(3, c.Diagnostics.Count);
        Assert.Contains(c.Diagnostics, d => d.Contains("BackgroundMaxKeep"));
    }

    [Fact]
    public async Task LoadAsync_missing_file_gives_empty()
    {
        var c = await SteamConfig.LoadAsync(Path.Combine(Path.GetTempPath(), "spp-nope-" + Guid.NewGuid(), "localconfig.vdf"), CancellationToken.None);
        Assert.Equal(SteamConfig.Empty, c);
    }

    [Fact]
    public async Task LoadAsync_reads_file_even_when_another_process_holds_it_open()
    {
        var dir = Directory.CreateTempSubdirectory("spp-cfg-").FullName;
        try
        {
            var path = Path.Combine(dir, "localconfig.vdf");
            await File.WriteAllTextAsync(path, Sample);
            using var held = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite | FileShare.Delete);
            var c = await SteamConfig.LoadAsync(path, CancellationToken.None);
            Assert.Equal(1, c.BackgroundRecordMode);
        }
        finally { Directory.Delete(dir, true); }
    }
}
