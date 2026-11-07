// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace SteamPoweredPlayer.Core.Diagnostics;

/// <summary>Outcome of one check of <c>--selftest</c> or <c>--bench</c>.</summary>
public enum CheckStatus
{
    Pass,
    Fail,
    /// <summary>Not checked here (belongs to a later phase, or the hardware lacks the feature); <see cref="CheckResult.Note"/> says why. Never a failure.</summary>
    Skip,
    /// <summary>A measurement without a pass/fail rule (reference runs, counts, slopes). Never a failure.</summary>
    Info,
}

/// <summary>
/// One line of a report. <see cref="Value"/> is the measured value as text (invariant culture); <see cref="Number"/> and
/// <see cref="Unit"/> carry the same measurement for the JSON; <see cref="Budget"/> is the limit it was compared with, if any.
/// No field may hold a path, a game name, an id or a machine name (see <see cref="Redactor"/>).
/// </summary>
public sealed record CheckResult(
    string Id,
    string Name,
    CheckStatus Status,
    string? Value = null,
    string? Note = null,
    double? Number = null,
    string? Unit = null,
    string? Budget = null)
{
    public static CheckResult Pass(string id, string name, string? value = null, string? note = null, double? number = null, string? unit = null, string? budget = null) =>
        new(id, name, CheckStatus.Pass, value, note, number, unit, budget);

    public static CheckResult Fail(string id, string name, string? value = null, string? note = null, double? number = null, string? unit = null, string? budget = null) =>
        new(id, name, CheckStatus.Fail, value, note, number, unit, budget);

    public static CheckResult Skip(string id, string name, string reason) => new(id, name, CheckStatus.Skip, null, reason);

    public static CheckResult Info(string id, string name, string? value = null, string? note = null, double? number = null, string? unit = null) =>
        new(id, name, CheckStatus.Info, value, note, number, unit);

    /// <summary>Pass or Fail from a condition.</summary>
    public static CheckResult From(bool ok, string id, string name, string? value = null, string? note = null, double? number = null, string? unit = null, string? budget = null) =>
        new(id, name, ok ? CheckStatus.Pass : CheckStatus.Fail, value, note, number, unit, budget);
}

/// <summary>
/// The report of a run: its lines in order, the summary counts and the exit code rule (0 only when nothing failed and the run
/// did not abort). Formats itself as text and JSON; both are pure and byte-stable for the same content (tested).
/// </summary>
public sealed class DiagnosticReport(string kind, string title)
{
    private readonly List<CheckResult> _checks = [];
    private readonly List<string> _headers = [];
    private readonly object _gate = new();

    /// <summary>"selftest" or "bench".</summary>
    public string Kind { get; } = kind;

    public string Title { get; } = title;

    /// <summary>True when the run could not finish (an exception, a missing prerequisite). Forces a non-zero exit.</summary>
    public bool Aborted { get; private set; }

    public string? AbortReason { get; private set; }

    public IReadOnlyList<CheckResult> Checks { get { lock (_gate) return [.. _checks]; } }

    /// <summary>A free-text line printed under the title (hardware class, versions, method notes).</summary>
    public void AddHeader(string line) { lock (_gate) _headers.Add(line); }

    public void Add(CheckResult c) { lock (_gate) _checks.Add(c); }

    public void Abort(string reason)
    {
        lock (_gate) { Aborted = true; AbortReason = reason; }
    }

    public int Count(CheckStatus s) { lock (_gate) return _checks.Count(c => c.Status == s); }

    /// <summary>0 when no check failed and the run was not aborted; 1 otherwise. (The caller maps an unexpected crash to 2.)</summary>
    public int ExitCode { get { lock (_gate) return Aborted || _checks.Any(c => c.Status == CheckStatus.Fail) ? 1 : 0; } }

    public string Summary()
    {
        var inv = CultureInfo.InvariantCulture;
        var text = string.Create(inv, $"{Count(CheckStatus.Pass)} passed, {Count(CheckStatus.Fail)} failed, {Count(CheckStatus.Skip)} skipped, {Count(CheckStatus.Info)} info");
        return Aborted ? text + ", ABORTED" : text;
    }

    /// <summary>The label of a status as printed: <c>PASS</c>, <c>FAIL</c>, <c>SKIP (reason)</c>, <c>INFO</c>.</summary>
    public static string Label(CheckResult c) => c.Status switch
    {
        CheckStatus.Pass => "PASS",
        CheckStatus.Fail => "FAIL",
        CheckStatus.Skip => c.Note is { Length: > 0 } n ? $"SKIP ({n})" : "SKIP",
        _ => "INFO",
    };

    /// <summary>The human-readable report: title, headers, one line per check, then the summary and the exit code.</summary>
    public string ToText()
    {
        List<CheckResult> checks;
        List<string> headers;
        bool aborted;
        string? reason;
        lock (_gate) { checks = [.. _checks]; headers = [.. _headers]; aborted = Aborted; reason = AbortReason; }

        var sb = new StringBuilder();
        sb.Append(Title).Append('\n');
        foreach (var h in headers) sb.Append(h).Append('\n');
        sb.Append('\n');
        var width = Math.Min(44, checks.Count == 0 ? 0 : checks.Max(c => c.Name.Length));
        foreach (var c in checks)
        {
            var label = Label(c);
            sb.Append(label.PadRight(c.Status == CheckStatus.Skip ? 18 : 5)).Append(' ');
            sb.Append(c.Name.PadRight(width));
            if (c.Value is { Length: > 0 } v) sb.Append("  ").Append(v);
            if (c.Budget is { Length: > 0 } b) sb.Append("  (budget ").Append(b).Append(')');
            if (c.Status != CheckStatus.Skip && c.Note is { Length: > 0 } n) sb.Append("  - ").Append(n);
            sb.Append('\n');
        }
        sb.Append('\n');
        if (aborted) sb.Append("ABORTED: ").Append(reason).Append('\n');
        sb.Append("Summary: ").Append(Summary()).Append('\n');
        sb.Append("Result: ").Append(ExitCode == 0 ? "PASS" : "FAIL").Append(" (exit code ").Append(ExitCode.ToString(CultureInfo.InvariantCulture)).Append(")\n");
        return sb.ToString();
    }

    /// <summary>The JSON report: <c>kind</c>, <c>title</c>, <c>headers</c>, <c>checks</c> (with numbers), <c>summary</c>, <c>exitCode</c>.</summary>
    public byte[] ToJson()
    {
        List<CheckResult> checks;
        List<string> headers;
        lock (_gate) { checks = [.. _checks]; headers = [.. _headers]; }
        using var ms = new MemoryStream();
        using (var w = new Utf8JsonWriter(ms, new JsonWriterOptions { Indented = true }))
        {
            w.WriteStartObject();
            w.WriteString("kind", Kind);
            w.WriteString("title", Title);
            w.WriteStartArray("headers");
            foreach (var h in headers) w.WriteStringValue(h);
            w.WriteEndArray();
            w.WriteStartArray("checks");
            foreach (var c in checks)
            {
                w.WriteStartObject();
                w.WriteString("id", c.Id);
                w.WriteString("name", c.Name);
                w.WriteString("status", c.Status.ToString().ToLowerInvariant());
                if (c.Value is not null) w.WriteString("value", c.Value);
                if (c.Number is { } num && double.IsFinite(num)) w.WriteNumber("number", num);
                if (c.Unit is not null) w.WriteString("unit", c.Unit);
                if (c.Budget is not null) w.WriteString("budget", c.Budget);
                if (c.Note is not null) w.WriteString("note", c.Note);
                w.WriteEndObject();
            }
            w.WriteEndArray();
            w.WriteStartObject("summary");
            w.WriteNumber("passed", Count(CheckStatus.Pass));
            w.WriteNumber("failed", Count(CheckStatus.Fail));
            w.WriteNumber("skipped", Count(CheckStatus.Skip));
            w.WriteNumber("info", Count(CheckStatus.Info));
            w.WriteBoolean("aborted", Aborted);
            w.WriteEndObject();
            w.WriteNumber("exitCode", ExitCode);
            w.WriteEndObject();
        }
        return ms.ToArray();
    }

    /// <summary>Writes <paramref name="textPath"/> and, beside it, the same name with <c>.json</c>. Safe to call after every check.</summary>
    public void WriteTo(string textPath)
    {
        var full = Path.GetFullPath(textPath);
        Directory.CreateDirectory(Path.GetDirectoryName(full)!);
        File.WriteAllText(full, ToText());
        File.WriteAllBytes(JsonPathFor(full), ToJson());
    }

    /// <summary>The JSON file that goes with a text report: same name, <c>.json</c> extension.</summary>
    public static string JsonPathFor(string textPath) =>
        textPath.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ? textPath + ".json" : Path.ChangeExtension(textPath, ".json");
}
