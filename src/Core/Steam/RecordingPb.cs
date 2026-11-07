// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Steam;

/// <summary>Field 1.5 of <c>gamerecording.pb</c> / <c>clip.pb</c>: the bg session behind a timeline. <see cref="OffsetMs"/> (field 10) is the exact one.</summary>
public sealed record SessionRef(string? BgFolder, long? StartMs, long? DurationMs, long? OffsetMs, PbMessage Raw);

/// <summary>One timeline entry of <c>gamerecording.pb</c> field 1, or the single one of <c>clip.pb</c>. <see cref="Session"/> is null when field 5 is missing.</summary>
public sealed record TimelineRef(string? TimelineId, ulong? GameId, long? DateRecordedUnix, long? DurationMs, SessionRef? Session, PbMessage Raw);

public sealed record PbTag(string? Name, string? Group, string? Icon, long? Priority);

/// <summary>Where a tag occurs: timeline id and offset in ms.</summary>
public sealed record PbTagOccurrence(string? TimelineId, long? OffsetMs);

public sealed record PbTagIndexEntry(ulong? AppId, PbTag? Tag, IReadOnlyList<PbTagOccurrence> Occurrences, PbMessage Raw);

/// <summary>Parsed <c>gamerecording.pb</c>. <see cref="Raw"/> keeps every field (also unknown ones); <c>Raw.Serialize()</c> equals the input.</summary>
public sealed record GameRecordingPb(
    IReadOnlyList<TimelineRef> Timelines, IReadOnlyList<PbTagIndexEntry> TagIndex, PbMessage Raw, IReadOnlyList<string> Diagnostics)
{
    /// <summary>The exact offset (field 1.5.10, ms) for <paramref name="timelineId"/>, or null when unknown (missing field 5, trimmed away, no entry).</summary>
    public long? OffsetMsFor(string timelineId) =>
        Timelines.FirstOrDefault(t => string.Equals(t.TimelineId, timelineId, StringComparison.Ordinal))?.Session?.OffsetMs;

    /// <summary>The exact offset for the session whose cache folder is <paramref name="bgFolder"/> (e.g. <c>bg_730_20250101_120000</c>).</summary>
    public long? OffsetMsForFolder(string bgFolder) =>
        Timelines.FirstOrDefault(t => string.Equals(t.Session?.BgFolder, bgFolder, StringComparison.Ordinal))?.Session?.OffsetMs;
}

/// <summary>Parsed <c>clip.pb</c>.</summary>
public sealed record ClipPb(
    TimelineRef? Timeline, long? CreatedUnix, ulong? GameId, string? Title, int? Width, int? Height, PbMessage Raw, IReadOnlyList<string> Diagnostics)
{
    /// <summary>Exact offset (field 1.5.10, ms) of the clip's source bg start inside the timeline, if present.</summary>
    public long? OffsetMs => Timeline?.Session?.OffsetMs;
}

/// <summary>
/// Reader for Steam's <c>gamerecording.pb</c> and <c>clip.pb</c> (schema: <c>Proto/steamrecording.proto</c>, documentation only).
/// Hand-written on the wire format: unknown fields are kept, odd wire types are tolerated, nothing throws on bad input; problems are
/// listed in <c>Diagnostics</c>.
/// </summary>
public static class RecordingPb
{
    public static GameRecordingPb ParseGameRecording(ReadOnlyMemory<byte> data)
    {
        var d = new List<string>();
        var raw = PbMessage.Parse(data);
        if (raw.Error is not null) d.Add(raw.Error);

        var timelines = new List<TimelineRef>();
        foreach (var f in raw.All(1))
            if (SubMessage(f, "1", d) is { } m) timelines.Add(ReadTimelineRef(m, "1", d));

        var tags = new List<PbTagIndexEntry>();
        foreach (var f in raw.All(4))
            if (SubMessage(f, "4", d) is { } m) tags.Add(ReadTagIndexEntry(m, d));

        return new(timelines, tags, raw, d);
    }

    public static ClipPb ParseClip(ReadOnlyMemory<byte> data)
    {
        var d = new List<string>();
        var raw = PbMessage.Parse(data);
        if (raw.Error is not null) d.Add(raw.Error);

        TimelineRef? tl = raw.First(1) is { } f1 && SubMessage(f1, "1", d) is { } m ? ReadTimelineRef(m, "1", d) : null;
        long? created = Int64(raw, 3, "", d);
        ulong? gameId = UInt64(raw, 4, "", d);
        string? title = Text(raw, 7, "", d);
        long? w = Int64(raw, 12, "", d), h = Int64(raw, 13, "", d);
        return new(tl, created, gameId, title, ToInt(w), ToInt(h), raw, d);
    }

    public static async Task<GameRecordingPb> ReadGameRecordingAsync(string path, CancellationToken ct = default)
    {
        var (bytes, err) = await TryReadAsync(path, ct).ConfigureAwait(false);
        if (bytes is null) return new([], [], PbMessage.Empty, [err!]);
        return ParseGameRecording(bytes);
    }

    public static async Task<ClipPb> ReadClipAsync(string path, CancellationToken ct = default)
    {
        var (bytes, err) = await TryReadAsync(path, ct).ConfigureAwait(false);
        if (bytes is null) return new(null, null, null, null, null, null, PbMessage.Empty, [err!]);
        return ParseClip(bytes);
    }

    /// <summary>Steam may trim or rewrite the file while we read: share everything, report IO problems as a diagnostic.</summary>
    private static async Task<(byte[]? bytes, string? error)> TryReadAsync(string path, CancellationToken ct)
    {
        try
        {
            await using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete,
                4096, FileOptions.Asynchronous | FileOptions.SequentialScan);
            var ms = new MemoryStream();
            await fs.CopyToAsync(ms, ct).ConfigureAwait(false);
            return (ms.ToArray(), null);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            return (null, $"cannot read {Path.GetFileName(path)}: {e.Message}");
        }
    }

    private static TimelineRef ReadTimelineRef(PbMessage m, string path, List<string> d)
    {
        SessionRef? session = null;
        if (m.First(5) is { } f5 && SubMessage(f5, path + ".5", d) is { } sm)
        {
            string p = path + ".5";
            session = new(Text(sm, 1, p, d), Int64(sm, 2, p, d), Int64(sm, 3, p, d), Int64(sm, 10, p, d), sm);
        }
        return new(Text(m, 1, path, d), UInt64(m, 2, path, d), Int64(m, 3, path, d), Int64(m, 4, path, d), session, m);
    }

    private static PbTagIndexEntry ReadTagIndexEntry(PbMessage m, List<string> d)
    {
        PbTag? tag = null;
        if (m.First(2) is { } f2 && SubMessage(f2, "4.2", d) is { } tm)
            tag = new(Text(tm, 1, "4.2", d), Text(tm, 2, "4.2", d), Text(tm, 3, "4.2", d), Int64(tm, 4, "4.2", d));
        var occ = new List<PbTagOccurrence>();
        foreach (var f in m.All(3))
            if (SubMessage(f, "4.3", d) is { } om) occ.Add(new(Text(om, 2, "4.3", d), Int64(om, 3, "4.3", d)));
        return new(UInt64(m, 1, "4", d), tag, occ, m);
    }

    private static PbMessage? SubMessage(PbField f, string path, List<string> d)
    {
        if (f.WireType != PbField.LengthDelimited) { d.Add($"{path}: expected a message, got wire type {f.WireType}"); return null; }
        var m = PbMessage.Parse(f.Payload);
        if (m.Error is not null) { d.Add($"{path}: {m.Error}"); return null; }
        return m;
    }

    private static string FieldPath(string path, int n) => path.Length == 0 ? n.ToString() : $"{path}.{n}";

    private static ulong? UInt64(PbMessage m, int n, string path, List<string> d)
    {
        if (m.First(n) is not { } f) return null;
        var v = f.AsUInt64();
        if (v is null) d.Add($"{FieldPath(path, n)}: not a number (wire type {f.WireType})");
        return v;
    }

    private static long? Int64(PbMessage m, int n, string path, List<string> d)
    {
        if (m.First(n) is not { } f) return null;
        var v = f.AsInt64();
        if (v is null) d.Add($"{FieldPath(path, n)}: not a number (wire type {f.WireType})");
        return v;
    }

    private static string? Text(PbMessage m, int n, string path, List<string> d)
    {
        if (m.First(n) is not { } f) return null;
        var v = f.AsString();
        if (v is null) d.Add($"{FieldPath(path, n)}: not text (wire type {f.WireType})");
        return v;
    }

    private static int? ToInt(long? v) => v is >= int.MinValue and <= int.MaxValue ? (int)v : null;
}
