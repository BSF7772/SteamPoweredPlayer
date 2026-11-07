// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Diagnostics;

public enum DiagnosticsCommand { SelfTest, Bench, RealCheck, UiCheck }

/// <summary>A parsed <c>--selftest</c>, <c>--bench</c>, <c>--realcheck</c> or <c>--uicheck</c> command line.</summary>
/// <param name="ReportPath">The text report; the JSON report goes beside it (<see cref="DiagnosticReport.JsonPathFor"/>).</param>
/// <param name="Library">Bench and realcheck: <c>--library &lt;BackgroundRecordPath&gt;</c> (else <c>SPP_REAL_LIBRARY</c>, else Steam's setting).</param>
/// <param name="Quick">Bench only: <c>--quick</c>, the one-minute version.</param>
/// <param name="Captures">Uicheck only: <c>--captures &lt;dir&gt;</c> for the window captures (default: a folder in %TEMP%, never the repo).</param>
/// <param name="Child">Uicheck only: <c>--uicheck-child</c>, the process with the window (the parent watches it for crashes).</param>
public sealed record DiagnosticsRequest(DiagnosticsCommand Command, string ReportPath, string? Library = null, bool Quick = false,
    string? Captures = null, bool Child = false);

/// <summary>
/// The command line side of the diagnostics. The app calls <see cref="TryParse"/> before it creates any window; when it returns a
/// request, <see cref="Run"/> executes it and the app exits with the returned code (0 = all passed, 1 = a check failed or the
/// run aborted, 2 = the run crashed).
/// </summary>
public static class DiagnosticsCli
{
    /// <summary><paramref name="args"/> are the arguments after the program name. Null when no diagnostics command is present.</summary>
    public static DiagnosticsRequest? TryParse(IReadOnlyList<string> args)
    {
        for (var i = 0; i < args.Count; i++)
        {
            var flag = args[i].ToLowerInvariant();
            var cmd = flag switch
            {
                "--selftest" => (DiagnosticsCommand?)DiagnosticsCommand.SelfTest,
                "--bench" => DiagnosticsCommand.Bench,
                "--realcheck" => DiagnosticsCommand.RealCheck,
                "--uicheck" or "--uicheck-child" => DiagnosticsCommand.UiCheck,
                _ => null,
            };
            if (cmd is not { } c) continue;
            var name = c switch
            {
                DiagnosticsCommand.SelfTest => "selftest",
                DiagnosticsCommand.Bench => "bench",
                DiagnosticsCommand.RealCheck => "realcheck",
                _ => "uicheck",
            };
            var path = i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal)
                ? args[i + 1]
                : Path.Combine(Path.GetTempPath(), $"SteamPoweredPlayer-{name}.txt");
            string? captures = c == DiagnosticsCommand.UiCheck
                ? Value(args, "--captures") ?? Path.Combine(Path.GetTempPath(), "SteamPoweredPlayer-uicheck")
                : null;
            return new(c, path, Value(args, "--library"), IndexOf(args, "--quick") >= 0, captures, flag == "--uicheck-child");
        }
        return null;
    }

    /// <summary>The command line of the uicheck child process (the one with the window).</summary>
    public static IReadOnlyList<string> ChildArguments(DiagnosticsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        var a = new List<string> { "--uicheck-child", request.ReportPath };
        if (request.Captures is { } c) { a.Add("--captures"); a.Add(c); }
        return a;
    }

    private static string? Value(IReadOnlyList<string> args, string flag)
    {
        var i = IndexOf(args, flag);
        return i >= 0 && i + 1 < args.Count && !args[i + 1].StartsWith("--", StringComparison.Ordinal) ? args[i + 1] : null;
    }

    private static int IndexOf(IReadOnlyList<string> args, string flag)
    {
        for (var i = 0; i < args.Count; i++)
            if (string.Equals(args[i], flag, StringComparison.OrdinalIgnoreCase)) return i;
        return -1;
    }

    /// <summary>
    /// Runs a headless request (<c>--selftest</c>, <c>--bench</c>, <c>--realcheck</c>) on a thread-pool thread (no UI synchronization
    /// context) and returns the exit code. <c>--uicheck</c> needs the app's window: the app runs it (Services/UiCheck).
    /// </summary>
    public static int Run(DiagnosticsRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Command == DiagnosticsCommand.UiCheck) throw new ArgumentException("--uicheck is run by the app, not headless", nameof(request));
        try
        {
            return Task.Run(async () =>
            {
                var full = Path.GetFullPath(request.ReportPath);
                Directory.CreateDirectory(Path.GetDirectoryName(full)!);
                return request.Command switch
                {
                    DiagnosticsCommand.SelfTest => await SelfTest.RunAsync(full).ConfigureAwait(false),
                    DiagnosticsCommand.RealCheck => await RealCheck.RunAsync(full, new RealCheckOptions(request.Library)).ConfigureAwait(false),
                    _ => await Bench.RunAsync(full, new BenchOptions(request.Library, request.Quick)).ConfigureAwait(false),
                };
            }).GetAwaiter().GetResult();
        }
        catch (Exception e)
        {
            try { File.AppendAllText(request.ReportPath, Environment.NewLine + "CRASH " + Redactor.Describe(e) + Environment.NewLine); } catch (IOException) { }
            return 2;
        }
    }
}
