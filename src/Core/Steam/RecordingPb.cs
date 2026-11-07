// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Steam;

/// <summary>
/// One recording of a timeline (Steam: <c>CGameRecordingTimelineMetadata_Recording</c>, field 5 of the timeline, repeated).
/// <see cref="BgFolder"/> = <c>recording_id</c> (1), the media folder name; <see cref="StartMs"/> = <c>start_offset_ms</c> (2);
/// <see cref="DurationMs"/> = <c>duration_ms</c> (3); <see cref="RecordingType"/> = <c>recording_type</c> (4; seen: 3 = background
/// cache session, 4 = clip recording); <see cref="OffsetMs"/> = <c>recording_zero_timeline_offset_ms</c> (10), the exact offset.
/// </summary>
public sealed record SessionRef(string? BgFolder, long? StartMs, long? DurationMs, long? OffsetMs, PbMessage Raw)
{
    public long? RecordingType => Raw.First(4)?.AsInt64();
    /// <summary><c>file_size</c> (9).</summary>
    public long? FileSize => Raw.First(9)?.AsInt64();
}

/// <summary>
/// One timeline (Steam: <c>CGameRecordingTimelineMetadata</c>): <c>timeline_id</c> (1), <c>game_id</c> (2), <c>date_recorded</c> (3),
/// <c>duration_ms</c> (4), <c>recordings</c> (5, repeated). <see cref="Session"/> is the first recording (null when there is none).
/// </summary>
public sealed record TimelineRef(string? TimelineId, ulong? GameId, long? DateRecordedUnix, long? DurationMs, SessionRef? Session, PbMessage Raw)
{
    public IReadOnlyList<SessionRef> Recordings { get; init; } = Session is null ? [] : [Session];
}

public sealed record PbTag(string? Name, string? Group, string? Icon, long? Priority);

/// <summary>Where a tag occurs: timeline id and offset in ms.</summary>
public sealed record PbTagOccurrence(string? TimelineId, long? OffsetMs);

public sealed record PbTagIndexEntry(ulong? AppId, PbTag? Tag, IReadOnlyList<PbTagOccurrence> Occurrences, PbMessage Raw);

/// <summary>Parsed <c>gamerecording.pb</c>. <see cref="Raw"/> keeps every field (also unknown ones); <c>Raw.Serialize()</c> equals the input.</summary>
public sealed record GameRecordingPb(
    IReadOnlyList<TimelineRef> Timelines, IReadOnlyList<PbTagIndexEntry> TagIndex, PbMessage Raw, IReadOnlyList<string> Diagnostics)
{
    /// <summary>The exact offset (field 1.5.10, ms) of the first recording of <paramref name="timelineId"/>, or null when unknown.</summary>
    public long? OffsetMsFor(string timelineId) =>
        Timelines.FirstOrDefault(t => string.Equals(t.TimelineId, timelineId, StringComparison.Ordinal))?.Session?.OffsetMs;

    /// <summary>The exact offset for the recording whose media folder is <paramref name="bgFolder"/>, searching every recording of every timeline.</summary>
    public long? OffsetMsForFolder(string bgFolder) => RecordingNamed(Timelines, bgFolder)?.OffsetMs;

    internal static SessionRef? RecordingNamed(IEnumerable<TimelineRef> timelines, string folder) =>
        timelines.SelectMany(t => t.Recordings).FirstOrDefault(r => string.Equals(r.BgFolder, folder, StringComparison.Ordinal));
}

/// <summary>
/// Parsed <c>clip.pb</c> (Steam: <c>CGameRecordingClipFile</c>): <c>timelines</c> (1, repeated), <c>first_timeline_start_offset_ms</c>
/// (2), <c>date_recorded</c> (3), <c>game_id</c> (4), <c>name</c> (7), <c>temporary</c> (8), <c>thumbnail_width</c>/<c>height</c> (12/13).
/// </summary>
public sealed record ClipPb(
    TimelineRef? Timeline, long? DateRecordedUnix, ulong? GameId, string? Title, int? ThumbnailWidth, int? ThumbnailHeight, PbMessage Raw,
    IReadOnlyList<string> Diagnostics)
{
    /// <summary>All timelines of the clip (<see cref="Timeline"/> is the first).</summary>
    public IReadOnlyList<TimelineRef> Timelines { get; init; } = Timeline is null ? [] : [Timeline];

    public long? FirstTimelineStartOffsetMs => Raw.First(2)?.AsInt64();
    public bool? Temporary => Raw.First(8)?.AsInt64() is { } v ? v != 0 : null;

    /// <summary>Exact offset (field 1.5.10, ms) of the clip's first recording inside its timeline, if present.</summary>
    public long? OffsetMs => Timeline?.Session?.OffsetMs;

    /// <summary>The media folder names (<c>recording_id</c>) of the clip, in order: the folders under <c>clip_*/video/</c>.</summary>
    public IEnumerable<string> MediaFolders => Timelines.SelectMany(t => t.Recordings).Select(r => r.BgFolder).OfType<string>();

    public long? OffsetMsForFolder(string folder) => GameRecordingPb.RecordingNamed(Timelines, folder)?.OffsetMs;
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

        var tls = new List<TimelineRef>();
        foreach (var f in raw.All(1))
            if (SubMessage(f, "1", d) is { } m) tls.Add(ReadTimelineRef(m, "1", d));
        long? date = Int64(raw, 3, "", d);
        ulong? gameId = UInt64(raw, 4, "", d);
        string? title = Text(raw, 7, "", d);
        long? w = Int64(raw, 12, "", d), h = Int64(raw, 13, "", d);
        return new(tls.FirstOrDefault(), date, gameId, title, ToInt(w), ToInt(h), raw, d) { Timelines = tls };
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
        var recordings = new List<SessionRef>();
        string p = path + ".5";
        foreach (var f5 in m.All(5))
            if (SubMessage(f5, p, d) is { } sm)
                recordings.Add(new(Text(sm, 1, p, d), Int64(sm, 2, p, d), Int64(sm, 3, p, d), Int64(sm, 10, p, d), sm));
        return new(Text(m, 1, path, d), UInt64(m, 2, path, d), Int64(m, 3, path, d), Int64(m, 4, path, d), recordings.FirstOrDefault(), m)
        { Recordings = recordings };
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
