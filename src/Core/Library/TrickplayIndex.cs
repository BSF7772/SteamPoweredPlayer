// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Library;

/// <summary>A thumbnail: the rectangle (<see cref="X"/>, <see cref="Y"/>, <see cref="Width"/>, <see cref="Height"/>) of <see cref="SheetPath"/> that shows <see cref="Chunk"/>.</summary>
public sealed record TrickplayTile(int Chunk, string SheetPath, int X, int Y, int Width, int Height);

/// <summary>
/// Reader for the trickplay of one recording (what the filmstrip and the scrub preview use). Time is on the media (tfdt) axis:
/// chunk N covers [(N − 1) · interval + shift, N · interval + shift), shift = <see cref="SessionMedia.AxisShiftSeconds"/> (0 for a cache
/// session; a saved clip restarts chunk numbers at 1 but keeps the session's tfdt). The manifest itself is keyed by chunk number only.
/// No I/O after construction.
/// </summary>
public sealed class TrickplayIndex
{
    private readonly TrickplayManifest _manifest;
    private readonly string _dir;
    private readonly Dictionary<int, (int Sheet, int Tile)> _byChunk = [];
    private readonly int[] _sorted;

    /// <param name="axisShiftSeconds">The recording's <see cref="SessionMedia.AxisShiftSeconds"/>, so lookups by media time hit the right chunk.</param>
    public TrickplayIndex(TrickplayManifest manifest, string recordingTrickDir, double axisShiftSeconds = 0)
    {
        _manifest = manifest;
        _dir = recordingTrickDir;
        AxisShiftSeconds = double.IsFinite(axisShiftSeconds) ? axisShiftSeconds : 0;
        for (var s = 0; s < manifest.Sheets.Count; s++)
        {
            var chunks = manifest.Sheets[s].Chunks;
            for (var t = 0; t < chunks.Count && t < manifest.Capacity; t++)
                if (chunks[t] > 0) _byChunk[chunks[t]] = (s, t);
        }
        _sorted = [.. _byChunk.Keys.Order()];
    }

    /// <summary>Null when the recording has no (readable, current-version) trickplay yet.</summary>
    /// <param name="axisShiftSeconds">The recording's <see cref="SessionMedia.AxisShiftSeconds"/>.</param>
    public static TrickplayIndex? TryLoad(DataFolder data, string recordingKey, double axisShiftSeconds = 0)
    {
        var dir = Trickplay.DirFor(data, recordingKey);
        var m = TrickplayManifest.TryRead(dir);
        return m is null ? null : new TrickplayIndex(m, dir, axisShiftSeconds);
    }

    /// <summary>Chunk N starts at (N − 1) · interval + this, as in the recording's <see cref="SessionMedia"/>.</summary>
    public double AxisShiftSeconds { get; }
    public double IntervalSeconds => _manifest.IntervalSeconds;
    public int TileWidth => _manifest.TileWidth;
    public int TileHeight => _manifest.TileHeight;
    public int Count => _sorted.Length;
    /// <summary>Chunk numbers that have a thumbnail, ascending.</summary>
    public IReadOnlyList<int> Chunks => _sorted;

    /// <summary>The chunk that contains media time <paramref name="seconds"/> (the formula of <see cref="SessionMedia.ChunkAt"/>), never below 1.</summary>
    public int ChunkAt(double seconds) => double.IsNaN(seconds)
        ? 1
        : (int)Math.Min(int.MaxValue, Math.Floor(Math.Max(0, seconds - AxisShiftSeconds) / _manifest.IntervalSeconds) + 1);

    public TrickplayTile? FindChunk(int chunk) =>
        _byChunk.TryGetValue(chunk, out var p) ? MakeTile(chunk, p.Sheet, p.Tile) : null;

    /// <summary>The thumbnail of the chunk that contains <paramref name="seconds"/>, or null in a gap / before the head / past the tail.</summary>
    public TrickplayTile? Find(double seconds) => FindChunk(ChunkAt(seconds));

    /// <summary>Like <see cref="Find"/>, but a gap resolves to the closest chunk that has a thumbnail (the earlier one on a tie). Null only when there is none at all.</summary>
    public TrickplayTile? FindNearest(double seconds)
    {
        if (_sorted.Length == 0) return null;
        var c = ChunkAt(seconds);
        var i = Array.BinarySearch(_sorted, c);
        if (i >= 0) return FindChunk(c);
        i = ~i; // first element greater than c
        if (i == 0) return FindChunk(_sorted[0]);
        if (i == _sorted.Length) return FindChunk(_sorted[^1]);
        var before = _sorted[i - 1];
        var after = _sorted[i];
        return FindChunk(c - before <= after - c ? before : after);
    }

    private TrickplayTile MakeTile(int chunk, int sheet, int tile) =>
        new(chunk, Path.Combine(_dir, _manifest.Sheets[sheet].File),
            tile % _manifest.Columns * _manifest.TileWidth, tile / _manifest.Columns * _manifest.TileHeight,
            _manifest.TileWidth, _manifest.TileHeight);
}
