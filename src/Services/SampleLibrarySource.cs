// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// Synthetic library with made-up games, for design work and screenshots (<c>--sample</c>). Nothing here comes from a real library.
/// Times are relative to "now" so the live session and the cache window look right whenever the app starts.
/// </summary>
public sealed class SampleLibrarySource : ILibrarySource
{
    private static readonly string[] Glyphs = ["💀", "🔫", "💣", "🏆", "📷", "⭐"];
    private static readonly string[] Labels = ["Elimination", "Multi kill", "Bomb planted", "Achievement", "Screenshot", "Highlight"];
    private static readonly string[] ClipTitles =
        ["Ace on Sample Map", "Clutch 1v3", "Last second win", "Funny physics", "Perfect landing", "Boss down", "Triple kill", "Great teamwork"];

    private readonly LibraryIndex _index;

    public SampleLibrarySource()
    {
        var now = DateTimeOffset.UtcNow;
        var games = new List<GameEntry>();
        var recs = new List<RecordingEntry>();

        // (name, clips, cache sessions, hours since the last recording)
        (string Name, int Clips, int Sessions, double AgeHours)[] spec =
        [
            ("Sample Game A", 8, 2, 0), ("Sample Game B", 5, 1, 3), ("Sample Racer", 3, 0, 20),
            ("Sample Tactics", 11, 0, 30), ("Pixel Quest Sample", 2, 0, 52), ("Sample Arena 2", 6, 1, 70),
            ("Sample Frontier", 1, 0, 140), ("Sample Space Sim", 4, 0, 200),
        ];

        for (var i = 0; i < spec.Length; i++)
        {
            var id = (900001 + i).ToString(CultureInfo.InvariantCulture);
            games.Add(new GameEntry(id, spec[i].Name));
            var last = now - TimeSpan.FromHours(spec[i].AgeHours);

            for (var c = 0; c < spec[i].Clips; c++)
            {
                var start = last - TimeSpan.FromHours(c * 5.5 + 0.7 + (i % 3));
                var dur = 15 + ((c * 37 + i * 11) % 75);
                recs.Add(new RecordingEntry(RecordingKind.Clip, id, start, $"clips/clip_{id}_{start:yyyyMMdd_HHmmss}",
                    [new SegmentRun(1, (int)Math.Ceiling(dur / 3.0))], DurationSeconds: dur,
                    Title: ClipTitles[(c + i) % ClipTitles.Length], MarkerCount: (c * 2 + i) % 5,
                    SizeBytes: dur * 1_400_000L));
            }

            for (var s = 0; s < spec[i].Sessions; s++)
            {
                // Newest session is live and ends now; the older one ended before a gap.
                var live = s == spec[i].Sessions - 1 && spec[i].AgeHours < 1;
                var durMin = live ? 52 : 34;
                var end = live ? now : now - TimeSpan.FromMinutes(20 + 46 * (spec[i].Sessions - 1 - s));
                var start = end - TimeSpan.FromMinutes(durMin);
                var dur = durMin * 60.0;
                recs.Add(new RecordingEntry(RecordingKind.CacheSession, id, start, $"video/bg_{id}_{start:yyyyMMdd_HHmmss}",
                    [new SegmentRun(1, (int)(dur / 3))], DurationSeconds: dur, Live: live, Temporary: true,
                    MarkerCount: 5, SizeBytes: (long)(dur * 1_400_000)));
            }
        }

        for (var d = 0; d < 2; d++)
        {
            var start = now - TimeSpan.FromDays(2 + d * 3);
            recs.Add(new RecordingEntry(RecordingKind.DownloadedClip, null, start, $"clips/clip_downloaded_{start:yyyyMMdd_HHmmss}",
                [new SegmentRun(1, 12)], DurationSeconds: 36, Title: "Clip from a friend", SizeBytes: 50_000_000));
        }

        _index = LibraryIndex.Create(Guid.Parse("00000000-0000-0000-0000-000000000001"), now, games, recs);
    }

    public LibraryIndex? Current => _index;
    public string? Problem => null;
    public event EventHandler? IndexChanged { add { } remove { } }
    public event EventHandler<ScanProgressInfo>? ScanProgress { add { } remove { } }

    public async Task StartAsync(CancellationToken ct = default)
    {
        if (_session is not null) return;
        _session = await SampleSession.CreateAsync(ct).ConfigureAwait(false);
        if (_session is null) return;
        // The live sample plays the same synthetic content: Steam "writes" a chunk every 3 s, then trims its head (a timer stands in
        // for the folder watcher; the Player page sees the same MediaChanged events a real library raises).
        _liveRel = _index.Recordings.FirstOrDefault(r => r.Live)?.RelativeFolder;
        if (_liveRel is not null) _liveTimer = new Timer(_ => LiveTick(), null, TimeSpan.FromSeconds(3), TimeSpan.FromSeconds(3));
    }

    public string? ResolveArt(string? relativePath) => null;

    // ---- Player page: every sample recording plays the synthetic session; the live one grows and is trimmed ----

    private SampleSession? _session;
    private string? _liveRel;
    private Timer? _liveTimer;
    private int _liveFirst = 1, _liveLast = 20;
    private readonly object _liveGate = new();

    public TrickplayQueue Trickplay { get; } = new();

    public event EventHandler<MediaChangedArgs>? MediaChanged;

    public SessionMedia? GetMedia(string relativeFolder)
    {
        if (_session is not { } s || _index.Recordings.All(r => r.RelativeFolder != relativeFolder)) return null;
        if (relativeFolder != _liveRel) return s.Media;
        lock (_liveGate)
            return new SessionMedia(s.Folder, [new SegmentRun(_liveFirst, _liveLast)], s.Media.SegmentSeconds, Live: true, Temporary: true);
    }

    public IReadOnlyList<Marker> FullMarkersFor(string relativeFolder) =>
        _session is { } s && _index.Recordings.Any(r => r.RelativeFolder == relativeFolder) ? s.Markers : [];

    public TrickplayIndex? TrickplayFor(string relativeFolder) => _session?.Trickplay;

    private void LiveTick()
    {
        string rel;
        lock (_liveGate)
        {
            if (_liveRel is null) return;
            rel = _liveRel;
            if (_liveLast < SampleSession.Chunks) _liveLast++;
            else if (_liveLast - _liveFirst + 1 > 15) _liveFirst += 2;
            else { _liveTimer?.Dispose(); _liveTimer = null; }
        }
        MediaChanged?.Invoke(this, new MediaChangedArgs(rel, GetMedia(rel)));
    }

    public IReadOnlyList<MarkerSpot> MarkersFor(RecordingEntry r)
    {
        if (r.Kind != RecordingKind.CacheSession || r.DurationSeconds <= 0) return [];
        var seed = r.RelativeFolder.Sum(c => c);
        var list = new List<MarkerSpot>();
        for (var k = 0; k < r.MarkerCount; k++)
        {
            var g = (seed + k * 3) % Glyphs.Length;
            var at = r.DurationSeconds * (0.08 + 0.84 * ((k + 0.2 + 0.6 * (((seed + k * 7) % 10) / 10.0)) / r.MarkerCount));
            list.Add(new MarkerSpot(at, Glyphs[g], Labels[g]));
        }
        return list.OrderBy(m => m.OffsetSeconds).ToList();
    }
}
