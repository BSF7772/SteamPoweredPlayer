// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Diagnostics;

public enum DiagnosticsCommand { SelfTest, Bench }

/// <summary>A parsed <c>--selftest</c> or <c>--bench</c> command line.</summary>
/// <param name="ReportPath">The text report; the JSON report goes beside it (<see cref="DiagnosticReport.JsonPathFor"/>).</param>
/// <param name="Library">Bench only: <c>--library &lt;BackgroundRecordPath&gt;</c> (else <c>SPP_REAL_LIBRARY</c>, else Steam's setting).</param>
/// <param name="Quick">Bench only: <c>--quick</c>, the one-minute version.</param>
public sealed record DiagnosticsRequest(DiagnosticsCommand Command, string ReportPath, string? Library = null, bool Quick = false);

/// <summary>
/// The command line side of the diagnostics. The app calls <see cref="TryParse"/> before it creates any window; when it returns a
/// request, <see cref="Run"/> executes it and the app exits with the returned code (0 = all passed, 1 = a check failed or the
/// run aborted, 2 = the run crashed).
/// </summary>
public static class DiagnosticsCli
{
    /// <summary><paramref name="args"/> are the arguments after the program name. Null when neither <c>--selftest</c> nor <c>--bench</c> is present.</summary>
    public static DiagnosticsRequest? TryParse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var cmd = args[i].ToLowerInvariant() switch
            {
                "--selftest" => (DiagnosticsCommand?)DiagnosticsCommand.SelfTest,
                "--bench" => DiagnosticsCommand.Bench,
                _ => null,
            };
            if (cmd is not { } c) continue;
            var path = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[i + 1]
                : Path.Combine(Path.GetTempPath(), c == DiagnosticsCommand.SelfTest ? "SteamPoweredPlayer-selftest.txt" : "SteamPoweredPlayer-bench.txt");
            string? library = null;
            var li = IndexOf(args, "--library");
            if (li >= 0 && li + 1 < args.Count && !args[li + 1].StartsWith("--", StringComparison.Ordinal)) library = args[li + 1];
            return new(c, path, library, IndexOf(args, "--quick") >= 0);
        }
        return null;
    }

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>Runs the request on a thread-pool thread (no UI synchronization context) and returns the exit code.</summary>
    public static int Run(DiagnosticsRequest request)
    {
        try
        {
            return Task.Run(async () =>
            {
                var full = Path.GetFullPath(request.ReportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                return request.Command == DiagnosticsCommand.SelfTest
                    ? await SelfTest.RunAsync(full).ConfigureAwait(false)
                    : await Bench.RunAsync(full, new BenchOptions(request.Library, request.Quick)).ConfigureAwait(false);
            }).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            try { File.AppendAllText(request.ReportPath, Environment.NewLine + "CRASH " + Redactor.Describe(e) + Environment.NewLine); } catch (IOException) { }
            return 2;
        }
    }
}
