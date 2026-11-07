// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Library;

/// <summary>Art paths relative to the data folder (forward slashes), e.g. <c>art/730/cover.jpg</c>. Null = not available.</summary>
public sealed record GameArt(string? Cover = null, string? Hero = null, string? HeroBlur = null, string? Logo = null);

/// <param name="GameId">Decimal string: shortcut ids exceed 2^53 and not every JSON consumer reads 64-bit integers exactly.</param>
public sealed record GameEntry(string GameId, string Name, bool IsShortcut = false, GameArt? Art = null);

/// <param name="RelativeFolder">Relative to BackgroundRecordPath, forward slashes, e.g. <c>video/bg_730_20250101_120000</c>.</param>
/// <param name="GameId">Null for downloaded clips (no game id in the name).</param>
/// <param name="Runs">Complete-chunk runs as <c>[first,last]</c> pairs.</param>
public sealed record RecordingEntry(
    RecordingKind Kind,
    string? GameId,
    DateTimeOffset StartUtc,
    string RelativeFolder,
    IReadOnlyList<SegmentRun> Runs,
    double SegmentSeconds = SessionMedia.DefaultSegmentSeconds,
    double DurationSeconds = 0,
    bool Live = false,
    bool Temporary = false,
    string? Title = null,
    int MarkerCount = 0,
    long? ExactOffsetMs = null,
    long SizeBytes = 0);

/// <summary><c>library.json</c> v1. Written only by the desktop; phones read it. See docs/data-folder.md.</summary>
public sealed record LibraryIndex(
    int SchemaVersion,
    string LibraryId,
    DateTimeOffset GeneratedUtc,
    IReadOnlyList<GameEntry> Games,
    IReadOnlyList<RecordingEntry> Recordings)
{
    public const int CurrentSchemaVersion = 1;

    public static LibraryIndex Create(Guid libraryId, DateTimeOffset generatedUtc, IEnumerable<GameEntry> games, IEnumerable<RecordingEntry> recordings) =>
        new(CurrentSchemaVersion, libraryId.ToString("D", CultureInfo.InvariantCulture), generatedUtc, games.ToList(), recordings.ToList());

    /// <summary>Indented, camelCase, LF newlines, UTC <c>Z</c> timestamps: byte-stable for a given model.</summary>
    public byte[] ToUtf8() => JsonSerializer.SerializeToUtf8Bytes(this, LibraryJson.Context.LibraryIndex);

    public static LibraryIndexParse Parse(ReadOnlySpan<byte> utf8)
    {
        int version;
        try
        {
            version = ReadSchemaVersion(utf8);
        }
        catch (JsonException e) { return new(LibraryIndexStatus.Corrupt, null, 0, e.Message); }

        if (version < 1) return new(LibraryIndexStatus.Corrupt, null, version, "schemaVersion missing or < 1");
        try
        {
            var index = JsonSerializer.Deserialize(utf8, LibraryJson.Context.LibraryIndex);
            if (index is null) return new(LibraryIndexStatus.Corrupt, null, version, "null document");
            return version > CurrentSchemaVersion
                ? new(LibraryIndexStatus.NewerSchema, index, version, null)   // best effort: unknown fields were ignored
                : new(LibraryIndexStatus.Ok, index, version, null);
        }
        catch (JsonException e)
        {
            return version > CurrentSchemaVersion
                ? new(LibraryIndexStatus.NewerSchema, null, version, e.Message)
                : new(LibraryIndexStatus.Corrupt, null, version, e.Message);
        }
    }

    private static int ReadSchemaVersion(ReadOnlySpan<byte> utf8)
    {
        var r = new Utf8JsonReader(utf8);
        if (!r.Read() || r.TokenType != JsonTokenType.StartObject) throw new JsonException("not a JSON object");
        while (r.Read())
        {
            if (r.TokenType == JsonTokenType.EndObject) break;
            if (r.TokenType != JsonTokenType.PropertyName) continue;
            var isVersion = r.ValueTextEquals("schemaVersion"u8);
            r.Read();
            if (isVersion) return r.TokenType == JsonTokenType.Number && r.TryGetInt32(out var v) ? v : 0;
            r.Skip();
        }
        return 0;
    }
}

public enum LibraryIndexStatus
{
    Ok,
    /// <summary>The file was written by a newer app. <see cref="LibraryIndexParse.Index"/> is best effort (may be null); show "update the app".</summary>
    NewerSchema,
    Corrupt,
    /// <summary>No library.json (IndexStore only).</summary>
    Missing,
}

public sealed record LibraryIndexParse(LibraryIndexStatus Status, LibraryIndex? Index, int SchemaVersion, string? Error);

// ---- JSON plumbing (source generated, no reflection) ----

internal static class LibraryJson
{
    internal static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        NewLine = "\n",
        PropertyNamingPolicy = JsonNamingPolicy.CamelCase,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        AllowTrailingCommas = true,
        ReadCommentHandling = JsonCommentHandling.Skip,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Skip,
        Converters = { new UtcInstantConverter(), new SegmentRunConverter(), new RecordingKindConverter() },
    };

    internal static readonly LibraryJsonContext Context = new(Options);
}

[JsonSerializable(typeof(LibraryIndex))]
internal sealed partial class LibraryJsonContext : JsonSerializerContext;

internal sealed class UtcInstantConverter : JsonConverter<DateTimeOffset>
{
    private const string Format = "yyyy-MM-dd'T'HH:mm:ss'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        var s = reader.GetString();
        if (s is null || !DateTimeOffset.TryParse(s, CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal, out var v))
            throw new JsonException("invalid UTC timestamp");
        return v;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}

/// <summary>A run is <c>[first,last]</c>.</summary>
internal sealed class SegmentRunConverter : JsonConverter<SegmentRun>
{
    public override SegmentRun Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartArray) throw new JsonException("run must be [first,last]");
        reader.Read(); var first = reader.GetInt32();
        reader.Read(); var last = reader.GetInt32();
        reader.Read();
        if (reader.TokenType != JsonTokenType.EndArray) throw new JsonException("run must have exactly two numbers");
        if (first < 0 || last < first) throw new JsonException("run bounds invalid");
        return new SegmentRun(first, last);
    }

    public override void Write(Utf8JsonWriter writer, SegmentRun value, JsonSerializerOptions options)
    {
        writer.WriteStartArray();
        writer.WriteNumberValue(value.First);
        writer.WriteNumberValue(value.Last);
        writer.WriteEndArray();
    }
}

/// <summary>Stable wire names, independent of the C# enum names: <c>cache</c>, <c>clip</c>, <c>downloadedClip</c>.</summary>
internal sealed class RecordingKindConverter : JsonConverter<RecordingKind>
{
    public override RecordingKind Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) => reader.GetString() switch
    {
        "cache" => RecordingKind.CacheSession,
        "clip" => RecordingKind.Clip,
        "downloadedClip" => RecordingKind.DownloadedClip,
        _ => throw new JsonException("unknown recording kind"),
    };

    public override void Write(Utf8JsonWriter writer, RecordingKind value, JsonSerializerOptions options) => writer.WriteStringValue(value switch
    {
        RecordingKind.CacheSession => "cache",
        RecordingKind.Clip => "clip",
        RecordingKind.DownloadedClip => "downloadedClip",
        _ => throw new JsonException("unknown recording kind"),
    });
}
