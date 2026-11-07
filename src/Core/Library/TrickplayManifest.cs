// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamPoweredPlayer.Core.Library;

/// <summary>Why a chunk has no thumbnail.</summary>
[JsonConverter(typeof(JsonStringEnumConverter<MissingReason>))]
public enum MissingReason
{
    ZeroFilled,
    /// <summary>Steam trimmed it (or it never existed) while we were working.</summary>
    Vanished,
    /// <summary>Shorter than its own first fragment (still being written, or cut).</summary>
    Truncated,
    Unreadable,
    /// <summary>Not the moof+mdat layout we understand.</summary>
    Invalid,
    /// <summary>Fed to ffmpeg but no frame came out (decode error).</summary>
    NoFrame,
}

public sealed record MissingChunk(int Chunk, MissingReason Reason);

/// <summary>
/// One JPEG sprite sheet, <c>Columns × Rows</c> tiles, row-major. <see cref="Chunks"/>[i] is the chunk shown in tile i
/// (0 = a tile that could not be attributed to a chunk; ignored by lookups). A sheet is "open" while it holds fewer tiles than its capacity;
/// only the last sheet can be open, and the next run rebuilds it.
/// </summary>
public sealed record TrickplaySheet(string File, IReadOnlyList<int> Chunks);

/// <summary>
/// <c>&lt;data&gt;/trick/&lt;recording-key&gt;/manifest.json</c>. Keyed by chunk number (chunk N is at media time (N − 1) · IntervalSeconds), so a
/// trimmed head, a growing tail and gaps need no special handling. Version 1.
/// </summary>
public sealed record TrickplayManifest(
    int Version, int TileWidth, int TileHeight, int Columns, int Rows, double IntervalSeconds,
    IReadOnlyList<TrickplaySheet> Sheets, IReadOnlyList<MissingChunk> Missing)
{
    public const int CurrentVersion = 1;
    public const string FileName = "manifest.json";

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = false };

    public int Capacity => Columns * Rows;

    public static TrickplayManifest Empty(int tileWidth, int columns, int rows, double intervalSeconds) =>
        new(CurrentVersion, tileWidth, 0, columns, rows, intervalSeconds, [], []);

    public string ToJson() => JsonSerializer.Serialize(this, Json);

    /// <summary>Null for malformed JSON, another version, or implausible values (the caller regenerates).</summary>
    public static TrickplayManifest? FromJson(string json)
    {
        try
        {
            var m = JsonSerializer.Deserialize<TrickplayManifest>(json, Json);
            if (m is null || m.Version != CurrentVersion || m.Columns <= 0 || m.Rows <= 0 || m.TileWidth <= 0 || m.TileHeight < 0
                || !(m.IntervalSeconds > 0) || m.Sheets is null || m.Missing is null) return null;
            return m;
        }
        catch (JsonException) { return null; }
    }

    public static TrickplayManifest? TryRead(string recordingTrickDir)
    {
        try
        {
            using var fs = new FileStream(Path.Combine(recordingTrickDir, FileName), FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            using var sr = new StreamReader(fs);
            return FromJson(sr.ReadToEnd());
        }
        catch (FileNotFoundException) { return null; }
        catch (DirectoryNotFoundException) { return null; }
    }

    /// <summary>Temp file + rename, so a reader never sees a half-written manifest.</summary>
    public void Write(string recordingTrickDir)
    {
        Directory.CreateDirectory(recordingTrickDir);
        var final = Path.Combine(recordingTrickDir, FileName);
        var tmp = final + ".tmp";
        File.WriteAllText(tmp, ToJson(), new System.Text.UTF8Encoding(false));
        File.Move(tmp, final, overwrite: true);
    }
}
