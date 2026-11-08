// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;
using SteamPoweredPlayer.Core;

namespace SteamPoweredPlayer.Services;

/// <summary>Reads the decoding choice from settings.json: <c>"GpuAuto"</c> (earlier builds) and <c>"Gpu"</c> both mean <see cref="DecodeMode.Gpu"/>.</summary>
public sealed class DecodeModeJsonConverter : JsonConverter<DecodeMode>
{
    public override DecodeMode Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        string.Equals(reader.GetString(), "Cpu", StringComparison.OrdinalIgnoreCase) ? DecodeMode.Cpu : DecodeMode.Gpu;

    public override void Write(Utf8JsonWriter writer, DecodeMode value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value == DecodeMode.Cpu ? "Cpu" : "GpuAuto");
}

/// <summary>
/// The app's own small settings file (per Windows user, not in Steam's folder and not in the shared data folder):
/// the confirmed data folder (plan D12), an optional recordings-folder override, and the decoding choice.
/// </summary>
public sealed class AppSettings
{
    /// <summary>Null until the user confirms the first-run proposal.</summary>
    public string? DataFolder { get; set; }

    /// <summary>Only used when Steam's own <c>BackgroundRecordPath</c> cannot be read.</summary>
    public string? RecordingsFolder { get; set; }

    /// <summary>GPU (D3D11VA, falls back to the CPU by itself) or CPU. Core's <see cref="DecodeMode"/> is the one shared type.</summary>
    [JsonConverter(typeof(DecodeModeJsonConverter))]
    public DecodeMode Decoding { get; set; } = DecodeMode.Gpu;

    /// <summary>Null until the user picks it on the first export (plan: no default folder).</summary>
    public string? ExportFolder { get; set; }

    /// <summary>FastEncoder preset id of the last export.</summary>
    public string? ExportPreset { get; set; }

    /// <summary>The user's FastEncoder.exe, chosen the first time "Open in FastEncoder" is used.</summary>
    public string? FastEncoderPath { get; set; }

    /// <summary>Audio delay in seconds applied to every recording of a game, by game id.</summary>
    public Dictionary<string, double> GameAudioDelays { get; set; } = [];

    /// <summary>Other PCs' recording folders, as the user chose them.</summary>
    public List<string> NetworkLibraries { get; set; } = [];

    /// <summary>Cap of the download cache; the least recently used chunks of unpinned recordings go first.</summary>
    public int DownloadCacheGb { get; set; } = 50;

    /// <summary>Set by "Don't show again" on the metered-network notice (plan §4.3: that notice is all the app does about metered links).</summary>
    public bool HideMeteredNotice { get; set; }

    public static string FilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "SteamPoweredPlayer", "settings.json");

    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(FilePath))
                return JsonSerializer.Deserialize<AppSettings>(File.ReadAllText(FilePath)) ?? new AppSettings();
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { }
        return new AppSettings();
    }

    public void Save()
    {
        if (AppServices.Options.Sample || AppServices.Options.DataDir is not null) return;   // design and test modes never touch the real settings file
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(FilePath)!);
            var tmp = FilePath + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(this, new JsonSerializerOptions { WriteIndented = true }));
            File.Move(tmp, FilePath, overwrite: true);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { }
    }
}
