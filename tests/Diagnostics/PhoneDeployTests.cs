// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Diagnostics;

namespace SteamPoweredPlayer.Tests.Diagnostics;

public sealed class PhoneDeployTests
{
    [Fact]
    public void The_script_is_found_from_a_folder_below_the_checkout()
    {
        var root = Directory.CreateTempSubdirectory("spp-deploy-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "tools"));
            var script = Path.Combine(root.FullName, PhoneDeploy.ScriptPath);
            File.WriteAllText(script, "");
            var deep = Directory.CreateDirectory(Path.Combine(root.FullName, "src", "bin", "x64", "Release"));
            Assert.Equal(script, PhoneDeploy.FindScript(deep.FullName));
        }
        finally { root.Delete(true); }
    }

    [Fact]
    public void Without_a_checkout_there_is_no_script()
    {
        var root = Directory.CreateTempSubdirectory("spp-deploy-");
        try { Assert.Null(PhoneDeploy.FindScript(root.FullName)); }
        finally { root.Delete(true); }
    }

    [Fact]
    public void The_real_script_ships_with_this_checkout() =>
        Assert.NotNull(PhoneDeploy.FindScript(AppContext.BaseDirectory));

    [Fact]
    public async Task Lines_and_the_exit_code_come_back()
    {
        var root = Directory.CreateTempSubdirectory("spp-deploy-");
        try
        {
            Directory.CreateDirectory(Path.Combine(root.FullName, "tools"));
            var script = Path.Combine(root.FullName, PhoneDeploy.ScriptPath);
            File.WriteAllText(script, "param([string]$Word)\n[Console]::Out.WriteLine(\"said $Word\")\nexit 4\n");
            var lines = new List<string>();
            var code = await PhoneDeploy.RunAsync(script, ["-Word", "hello"], l => { lock (lines) lines.Add(l); }, default);
            Assert.Equal(4, code);
            Assert.Contains("said hello", lines);
        }
        finally { root.Delete(true); }
    }
}
