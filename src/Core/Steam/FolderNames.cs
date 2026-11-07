// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.RegularExpressions;

namespace SteamPoweredPlayer.Core.Steam;

public enum RecordingKind
{
    /// <summary><c>video/bg_*</c>: Steam's rolling background cache, trimmed by Steam.</summary>
    CacheSession,
    /// <summary><c>clips/clip_&lt;gameid&gt;_*</c>: a saved clip, permanent until the user deletes it.</summary>
    Clip,
    /// <summary><c>clips/clip_downloaded_*</c>: a clip received from another device (no game id in the name).</summary>
    DownloadedClip,
}

/// <summary>A recording folder name. <see cref="GameId"/> is null only for <see cref="RecordingKind.DownloadedClip"/>.</summary>
public sealed record RecordingName(RecordingKind Kind, ulong? GameId, DateTimeOffset StartUtc);

/// <summary><c>timeline_&lt;gameid&gt;&lt;yyyyMMdd&gt;_&lt;HHmmss&gt;.json</c> (no separator between game id and date).</summary>
public sealed record TimelineName(ulong GameId, DateTimeOffset StartUtc);

/// <summary>A clip's media folder: <c>bg_*</c>, <c>bg_*_&lt;n&gt;</c> (<see cref="Index"/>) or <c>fg_*</c> (<see cref="Foreground"/>).</summary>
public sealed record MediaFolderName(bool Foreground, ulong GameId, DateTimeOffset StartUtc, int? Index);

/// <summary><c>chunk-stream{0,1}-NNNNN.m4s</c> (stream 0 = video, 1 = audio). <see cref="Temporary"/> = a <c>.tmp</c> suffix.</summary>
public sealed record ChunkName(int Stream, int Number, bool Temporary);

/// <summary>Steam's folder and file names (docs/steam-recording-format.md). Pure: no I/O. All times are UTC.</summary>
public static partial class FolderNames
{
    [GeneratedRegex(@"^(bg|clip)_(\d+)_(\d{8})_(\d{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex RecordingRegex();

    [GeneratedRegex(@"^clip_downloaded_(\d{8})_(\d{6})$", RegexOptions.CultureInvariant)]
    private static partial Regex DownloadedClipRegex();

    [GeneratedRegex(@"^timeline_(\d+)(\d{8})_(\d{6})\.json$", RegexOptions.CultureInvariant)]
    private static partial Regex TimelineRegex();

    [GeneratedRegex(@"^(bg|fg)_(\d+)_(\d{8})_(\d{6})(?:_(\d+))?$", RegexOptions.CultureInvariant)]
    private static partial Regex MediaFolderRegex();

    [GeneratedRegex(@"^chunk-stream([01])-(\d+)\.m4s(\.tmp)?$", RegexOptions.CultureInvariant)]
    private static partial Regex ChunkRegex();

    /// <summary>Parses <c>bg_*</c>, <c>clip_&lt;gameid&gt;_*</c> and <c>clip_downloaded_*</c>. Unknown layouts return null (reported, never guessed).</summary>
    public static RecordingName? ParseRecording(string folderName)
    {
        var m = RecordingRegex().Match(folderName);
        if (m.Success)
        {
            if (!ulong.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId)) return null;
            if (ParseUtc(m.Groups[3].Value, m.Groups[4].Value) is not { } utc) return null;
            return new(m.Groups[1].Value == "bg" ? RecordingKind.CacheSession : RecordingKind.Clip, gameId, utc);
        }
        var d = DownloadedClipRegex().Match(folderName);
        if (d.Success && ParseUtc(d.Groups[1].Value, d.Groups[2].Value) is { } dutc)
            return new(RecordingKind.DownloadedClip, null, dutc);
        return null;
    }

    /// <summary>
    /// The media folder inside a clip (<c>clip_*/video/&lt;name&gt;</c>): <c>bg_&lt;gameid&gt;_&lt;date&gt;_&lt;time&gt;</c>, the same with an index
    /// suffix (<c>_0</c>), or <c>fg_…</c> (seen in real libraries; probably on-demand recordings). Null for anything else.
    /// </summary>
    public static MediaFolderName? ParseMediaFolder(string folderName)
    {
        var m = MediaFolderRegex().Match(folderName);
        if (!m.Success) return null;
        if (!ulong.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId)) return null;
        if (ParseUtc(m.Groups[3].Value, m.Groups[4].Value) is not { } utc) return null;
        int? index = m.Groups[5].Success && int.TryParse(m.Groups[5].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var i) ? i : null;
        return new(m.Groups[1].Value == "fg", gameId, utc, index);
    }

    public static TimelineName? ParseTimeline(string fileName)
    {
        var m = TimelineRegex().Match(fileName);
        if (!m.Success) return null;
        if (!ulong.TryParse(m.Groups[1].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var gameId)) return null;
        return ParseUtc(m.Groups[2].Value, m.Groups[3].Value) is { } utc ? new(gameId, utc) : null;
    }

    public static ChunkName? ParseChunk(string fileName)
    {
        var m = ChunkRegex().Match(fileName);
        if (!m.Success) return null;
        if (!int.TryParse(m.Groups[2].Value, NumberStyles.None, CultureInfo.InvariantCulture, out var n)) return null;
        return new(m.Groups[1].Value[0] - '0', n, m.Groups[3].Success);
    }

    /// <summary>The chunk file name Steam writes: <c>chunk-stream0-01840.m4s</c> (at least 5 digits).</summary>
    public static string ChunkFile(int stream, int number) =>
        string.Create(CultureInfo.InvariantCulture, $"chunk-stream{stream}-{number:00000}.m4s");

    public static string InitFile(int stream) => string.Create(CultureInfo.InvariantCulture, $"init-stream{stream}.m4s");

    private static DateTimeOffset? ParseUtc(string date, string time) =>
        DateTimeOffset.TryParseExact(date + time, "yyyyMMddHHmmss", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var utc) ? utc : null;
}
