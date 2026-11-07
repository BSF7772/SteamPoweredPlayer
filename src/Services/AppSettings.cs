// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SteamPoweredPlayer.Services;

public enum DecodeMode { GpuAuto, Cpu }

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

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public DecodeMode Decoding { get; set; } = DecodeMode.GpuAuto;

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
        if (AppServices.Options.Sample) return;   // design mode never touches the real settings file
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
