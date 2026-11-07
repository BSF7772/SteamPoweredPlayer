// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Json;

namespace SteamPoweredPlayer.Core.Steam;

public enum TimelineEntryType { Event, StateDescription, GameMode, Screenshot, Achievement, Phase, Error, Unknown }

public sealed record TimelineTag(string Name, string? Group, string? Icon, long? Priority);

/// <summary>One entry of a timeline file. <see cref="TimeMs"/> and <see cref="DurationMs"/> are on the timeline's own axis (ms).</summary>
public sealed record TimelineEntry(
    string? Id, long TimeMs, TimelineEntryType Type, string RawType, string? Title, string? Description, string? Icon,
    long? Priority, long DurationMs, bool PossibleClip, string? Mode, IReadOnlyList<TimelineTag> Tags, string? AchievementName, string? Handle);

/// <summary>A parsed <c>timelines/timeline_*.json</c>. Bad entries are skipped and listed in <see cref="Diagnostics"/>.</summary>
public sealed record TimelineFile(
    DateTimeOffset? DateRecordedUtc, long? StartTimeMs, long? EndTimeMs, IReadOnlyList<TimelineEntry> Entries, IReadOnlyList<string> Diagnostics);

/// <summary>Lenient reader for Steam's timeline JSON (numbers may be strings, unknown types are kept). Never throws on bad content.</summary>
public static class Timeline
{
    public static async Task<TimelineFile> ReadAsync(string path, CancellationToken ct = default)
    {
        byte[] bytes;
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var ms = new MemoryStream();
            await fs.CopyToAsync(ms, ct).ConfigureAwait(false);
            bytes = ms.ToArray();
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return new(null, null, null, [], [$"cannot read {Path.GetFileName(path)}: {e.Message}"]);
        }
        return Parse(bytes);
    }

    public static TimelineFile Parse(string json) => Parse(System.Text.Encoding.UTF8.GetBytes(json));

    public static TimelineFile Parse(ReadOnlySpan<byte> utf8)
    {
        if (utf8.Length >= 3 && utf8[0] == 0xEF && utf8[1] == 0xBB && utf8[2] == 0xBF) utf8 = utf8[3..];
        var d = new List<string>();
        try
        {
            using var doc = JsonDocument.Parse(utf8.ToArray(), new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return new(null, null, null, [], ["root is not an object"]);

            DateTimeOffset? date = null;
            if (root.TryGetProperty("daterecorded", out var dr))
            {
                if (Int64Of(dr) is { } secs && secs is > 0 and < 253402300799L) date = DateTimeOffset.FromUnixTimeSeconds(secs);
                else d.Add("daterecorded: not a unix time");
            }
            long? start = Get(root, "starttime", d), end = Get(root, "endtime", d);

            var entries = new List<TimelineEntry>();
            if (root.TryGetProperty("entries", out var arr) && arr.ValueKind == JsonValueKind.Array)
            {
                int i = 0;
                foreach (var e in arr.EnumerateArray())
                {
                    if (ReadEntry(e, i, d) is { } entry) entries.Add(entry);
                    i++;
                }
            }
            else if (root.TryGetProperty("entries", out var bad) && bad.ValueKind != JsonValueKind.Null) d.Add("entries: not an array");
            return new(date, start, end, entries, d);
        }
        catch (JsonException e)
        {
            return new(null, null, null, [], [$"invalid JSON: {e.Message}"]);
        }
    }

    private static TimelineEntry? ReadEntry(JsonElement e, int index, List<string> d)
    {
        string at = $"entries[{index}]";
        if (e.ValueKind != JsonValueKind.Object) { d.Add($"{at}: not an object"); return null; }
        var local = new List<string>();
        long? time = Get(e, "time", local, required: true);
        if (time is null) { d.Add($"{at}: no usable time, skipped"); return null; }
        d.AddRange(local.Select(m => $"{at}.{m}"));

        string rawType = Str(e, "type") ?? "";
        var type = rawType switch
        {
            "event" => TimelineEntryType.Event,
            "state_description" => TimelineEntryType.StateDescription,
            "gamemode" => TimelineEntryType.GameMode,
            "screenshot" => TimelineEntryType.Screenshot,
            "achievement" => TimelineEntryType.Achievement,
            "phase" => TimelineEntryType.Phase,
            "error" => TimelineEntryType.Error,
            _ => TimelineEntryType.Unknown,
        };

        var tags = new List<TimelineTag>();
        if (e.TryGetProperty("tags", out var ta) && ta.ValueKind == JsonValueKind.Array)
            foreach (var t in ta.EnumerateArray())
            {
                if (t.ValueKind == JsonValueKind.Object && Str(t, "name") is { } name)
                    tags.Add(new(name, Str(t, "group"), Str(t, "icon"), Get(t, "priority", local)));
                else d.Add($"{at}.tags: bad tag skipped");
            }

        long duration = Get(e, "duration", d) ?? 0;
        if (duration < 0) duration = 0;
        return new(Str(e, "id"), time.Value, type, rawType, Str(e, "title"), Str(e, "description"), Str(e, "icon"),
            Get(e, "priority", d), duration, BoolOf(e, "possible_clip"), Str(e, "mode"), tags, Str(e, "achievement_name"), Str(e, "handle"));
    }

    private static string? Str(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return null;
        return v.ValueKind switch
        {
            JsonValueKind.String => v.GetString(),
            JsonValueKind.Number => v.GetRawText(),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            _ => null,
        };
    }

    private static long? Get(JsonElement o, string name, List<string> d, bool required = false)
    {
        if (!o.TryGetProperty(name, out var v) || v.ValueKind == JsonValueKind.Null) return null;
        var r = Int64Of(v);
        if (r is null && !required) d.Add($"{name}: not a number");
        return r;
    }

    private static long? Int64Of(JsonElement v)
    {
        switch (v.ValueKind)
        {
            case JsonValueKind.Number:
                if (v.TryGetInt64(out var l)) return l;
                return v.TryGetDouble(out var dbl) && double.IsFinite(dbl) && Math.Abs(dbl) < 9e18 ? (long)Math.Round(dbl) : null;
            case JsonValueKind.String:
                var s = v.GetString()?.Trim();
                if (long.TryParse(s, NumberStyles.AllowLeadingSign, CultureInfo.InvariantCulture, out var p)) return p;
                return double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var q) && double.IsFinite(q) && Math.Abs(q) < 9e18
                    ? (long)Math.Round(q) : null;
            default: return null;
        }
    }

    private static bool BoolOf(JsonElement o, string name)
    {
        if (!o.TryGetProperty(name, out var v)) return false;
        return v.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.Number => v.TryGetDouble(out var x) && x != 0,
            JsonValueKind.String => v.GetString() is { } s && (s.Equals("true", StringComparison.OrdinalIgnoreCase) || s == "1"),
            _ => false,
        };
    }
}
