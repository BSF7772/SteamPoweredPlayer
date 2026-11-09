// SPDX-License-Identifier: GPL-3.0-or-later
using System.Diagnostics;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>
/// Runs <c>tools\deploy-phone.ps1</c> for Settings' "Install on phone": build, install over USB, start. The script comes with a source
/// checkout and needs its toolchain, so an installed copy of the app without one shows no button.
/// </summary>
public static class PhoneDeploy
{
    public const string ScriptPath = @"tools\deploy-phone.ps1";

    /// <summary>The script in <paramref name="startFolder"/> or the nearest folder above it, or null.</summary>
    public static string? FindScript(string startFolder)
    {
        for (var folder = new DirectoryInfo(startFolder); folder is not null; folder = folder.Parent)
        {
            var candidate = Path.Combine(folder.FullName, ScriptPath);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    /// <summary>The script's exit code. Every line it prints goes to <paramref name="line"/>, on a background thread.</summary>
    public static async Task<int> RunAsync(string script, IReadOnlyList<string> arguments, Action<string> line, CancellationToken ct)
    {
        var start = new ProcessStartInfo("powershell.exe")
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            WorkingDirectory = Path.GetDirectoryName(Path.GetDirectoryName(script))!,
        };
        foreach (var a in (string[])["-NoProfile", "-NonInteractive", "-ExecutionPolicy", "Bypass", "-File", script]) start.ArgumentList.Add(a);
        foreach (var a in arguments) start.ArgumentList.Add(a);

        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) line(e.Data); };
        process.ErrorDataReceived += (_, e) => { if (!string.IsNullOrWhiteSpace(e.Data)) line(e.Data); };
        process.Start();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        try
        {
            await process.WaitForExitAsync(ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            try { process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { /* already gone */ }
            throw;
        }
        return process.ExitCode;
    }
}
