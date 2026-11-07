// SPDX-License-Identifier: GPL-3.0-or-later
using System.Text.Json;

namespace SteamPoweredPlayer.Core.Playback;

/// <summary>Where the user stopped in one recording. <see cref="MediaSeconds"/> is on the media (tfdt) axis, so it survives Steam trimming the head.</summary>
public sealed record ResumeEntry(double MediaSeconds, double DurationSeconds, DateTimeOffset UpdatedUtc);

/// <summary>
/// Last position per recording key (<c>RecordingEntry.RelativeFolder</c>), kept in a small JSON file that is local to this desktop
/// (<c>%LocalAppData%\SteamPoweredPlayer\positions.json</c>, never the shared data folder: phones keep their own). <see cref="Set"/> only
/// updates memory; <see cref="Flush"/> writes (atomic temp + rename) and is called on pause, on leaving the player and on exit.
/// Thread-safe. A missing or damaged file means no positions.
/// </summary>
public sealed class ResumePositions
{
    /// <summary>A position this close to the start or the end is not resumed (the user watched it, or never began).</summary>
    public const double EdgeSeconds = 5;

    public const int MaxEntries = 500;

    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };

    private readonly string _path;
    private readonly object _gate = new();
    private Dictionary<string, ResumeEntry> _entries = new(StringComparer.Ordinal);
    private bool _dirty;

    public ResumePositions(string path)
    {
        _path = path ?? throw new ArgumentNullException(nameof(path));
        try
        {
            if (File.Exists(path))
            {
                var loaded = JsonSerializer.Deserialize<Dictionary<string, ResumeEntry>>(File.ReadAllText(path), Json);
                if (loaded is not null) _entries = new(loaded, StringComparer.Ordinal);
            }
        }
        catch (Exception e) when (e is IOException or JsonException or UnauthorizedAccessException) { /* start empty */ }
    }

    public string Path => _path;

    public int Count { get { lock (_gate) return _entries.Count; } }

    public ResumeEntry? Get(string key)
    {
        lock (_gate) return _entries.GetValueOrDefault(key);
    }

    public void Set(string key, double mediaSeconds, double durationSeconds, DateTimeOffset? now = null)
    {
        ArgumentException.ThrowIfNullOrEmpty(key);
        if (!double.IsFinite(mediaSeconds) || !double.IsFinite(durationSeconds)) return;
        lock (_gate)
        {
            _entries[key] = new ResumeEntry(Math.Round(mediaSeconds, 3), Math.Round(durationSeconds, 3), now ?? DateTimeOffset.UtcNow);
            _dirty = true;
            if (_entries.Count > MaxEntries)
                foreach (var old in _entries.OrderBy(kv => kv.Value.UpdatedUtc).Take(_entries.Count - MaxEntries).Select(kv => kv.Key).ToList())
                    _entries.Remove(old);
        }
    }

    public void Remove(string key)
    {
        lock (_gate) _dirty |= _entries.Remove(key);
    }

    /// <summary>Writes the file when something changed. False when there was nothing to write or the write failed (the app goes on).</summary>
    public bool Flush()
    {
        string text;
        lock (_gate)
        {
            if (!_dirty) return false;
            text = JsonSerializer.Serialize(_entries, Json);
            _dirty = false;
        }
        try
        {
            Directory.CreateDirectory(System.IO.Path.GetDirectoryName(_path)!);
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, text, new System.Text.UTF8Encoding(false));
            File.Move(tmp, _path, overwrite: true);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            lock (_gate) _dirty = true;
            return false;
        }
    }

    /// <summary>
    /// The media time to resume at, or null: no entry, the position was trimmed away (before <paramref name="mediaStartSeconds"/>), or it is
    /// within <see cref="EdgeSeconds"/> of the start or the end of the recording as it is now.
    /// </summary>
    public static double? ResumeMediaSeconds(ResumeEntry? entry, double mediaStartSeconds, double mediaEndSeconds)
    {
        if (entry is null) return null;
        var ui = entry.MediaSeconds - mediaStartSeconds;
        var length = mediaEndSeconds - mediaStartSeconds;
        if (ui < EdgeSeconds || ui > length - EdgeSeconds) return null;
        return entry.MediaSeconds;
    }
}
