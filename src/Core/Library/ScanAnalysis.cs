// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Library;

/// <summary>
/// The pure half of the scanner: a directory listing (plus head-check results and the manifest kind) in, a <see cref="ScanResult"/> out.
/// No I/O, no clock of its own (<c>now</c> is a parameter), so it is exact and fast (4 800 entries well under 20 ms).
/// </summary>
public static class ScanAnalysis
{
    /// <summary>
    /// "Seconds old" for both decisions: the newest chunk of a live session is at most this old, and a <c>.tmp</c> chunk older than this
    /// is a crash leftover (Steam finishes a 3 s chunk well within it).
    /// </summary>
    public static readonly TimeSpan RecentThreshold = TimeSpan.FromSeconds(15);

    private const string Mpd = "session.mpd";

    /// <summary>
    /// Chunk files whose head must be read: numbers that have both streams under final names. Pass the same listing to
    /// <see cref="Analyze"/> with their results.
    /// </summary>
    public static IReadOnlyList<string> HeadCandidates(IReadOnlyList<ScanEntry> entries)
    {
        var video = new HashSet<int>();
        var audio = new Dictionary<int, string>();
        var videoName = new Dictionary<int, string>();
        foreach (var e in entries)
        {
            if (FolderNames.ParseChunk(e.Name) is not { Temporary: false } c) continue;
            if (c.Stream == 0) { video.Add(c.Number); videoName[c.Number] = e.Name; }
            else audio[c.Number] = e.Name;
        }
        var list = new List<string>(audio.Count * 2);
        foreach (var (n, a) in audio)
            if (video.Contains(n)) { list.Add(videoName[n]); list.Add(a); }
        return list;
    }

    /// <summary>
    /// Complete = both streams without <c>.tmp</c> and both heads <see cref="HeadCheck.Valid"/>. Chunks whose heads vanished are dropped
    /// (reported, not an error). <paramref name="head"/> is only called for <see cref="HeadCandidates"/> names.
    /// </summary>
    /// <param name="folder">Folder path, kept in the result as given (the wrapper passes a full path).</param>
    /// <param name="entries">One directory listing of the folder (files only).</param>
    /// <param name="manifest">What the head of <c>session.mpd</c> says (the listing only tells whether it exists).</param>
    /// <param name="now">Current UTC time, for Live and stale <c>.tmp</c>.</param>
    /// <param name="head">Head-check result per chunk file name.</param>
    /// <param name="temporary">Override of the Temporary flag; null = the folder name is a cache session.</param>
    public static ScanResult Analyze(string folder, IReadOnlyList<ScanEntry> entries, ManifestKind manifest, DateTimeOffset now,
        Func<string, HeadCheck> head, bool? temporary = null)
    {
        var issues = new List<ScanIssue>();
        var unknown = new List<string>();
        var video = new Dictionary<int, string>();
        var audio = new Dictionary<int, string>();
        var tmpVideo = new HashSet<int>();
        var tmpAudio = new HashSet<int>();
        var hasInit = new bool[2];
        var hasMpd = false;
        DateTimeOffset? newest = null;
        var staleTmp = false;

        foreach (var e in entries)
        {
            var name = e.Name;
            if (name == Mpd) { hasMpd = true; continue; }
            if (name == "init-stream0.m4s") { hasInit[0] = true; continue; }
            if (name == "init-stream1.m4s") { hasInit[1] = true; continue; }
            if (FolderNames.ParseChunk(name) is not { } c) { unknown.Add(name); continue; }
            if (newest is null || e.LastWriteUtc > newest) newest = e.LastWriteUtc;
            if (c.Temporary)
            {
                (c.Stream == 0 ? tmpVideo : tmpAudio).Add(c.Number);
                if (now - e.LastWriteUtc > RecentThreshold)
                {
                    staleTmp = true;
                    issues.Add(new(ScanIssueKind.StaleTemporaryChunk, name, c.Number));
                }
            }
            else (c.Stream == 0 ? video : audio)[c.Number] = name;
        }

        if (manifest == ManifestKind.Missing && hasMpd) manifest = ManifestKind.Unreadable;   // listed but not read: neutral
        if (manifest == ManifestKind.ZeroFilled) issues.Add(new(ScanIssueKind.ZeroFilledManifest, Mpd));

        // Candidates and their heads.
        var complete = new List<int>(Math.Min(video.Count, audio.Count));
        foreach (var (n, vName) in video)
        {
            if (!audio.TryGetValue(n, out var aName))
            {
                // A fresh .tmp of the audio chunk is just "being written": silent. Otherwise it is missing.
                if (!tmpAudio.Contains(n)) issues.Add(new(ScanIssueKind.MissingAudioChunk, FolderNames.ChunkFile(1, n), n));
                continue;
            }
            var hv = head(vName);
            var ha = head(aName);
            if (hv == HeadCheck.Valid && ha == HeadCheck.Valid) { complete.Add(n); continue; }
            Report(issues, vName, n, hv);
            Report(issues, aName, n, ha);
        }
        foreach (var n in audio.Keys)
            if (!video.ContainsKey(n) && !tmpVideo.Contains(n))
                issues.Add(new(ScanIssueKind.MissingVideoChunk, FolderNames.ChunkFile(0, n), n));

        for (var s = 0; s < 2; s++)
            if (!hasInit[s]) issues.Add(new(ScanIssueKind.MissingInit, FolderNames.InitFile(s)));

        var live = manifest == ManifestKind.Dynamic && newest is { } nw && now - nw <= RecentThreshold;
        var crashed = !live && (manifest == ManifestKind.ZeroFilled || staleTmp);

        if (!hasInit[0] || !hasInit[1])
            return new(ScanStatus.NotPlayable, null, crashed, issues, unknown, manifest, newest);
        if (complete.Count == 0)
        {
            issues.Add(new(ScanIssueKind.NoCompleteChunks));
            return new(ScanStatus.NotPlayable, null, crashed, issues, unknown, manifest, newest);
        }

        complete.Sort();
        var media = new SessionMedia(folder, Runs(complete), SessionMedia.DefaultSegmentSeconds, live,
            temporary ?? IsCacheSession(folder));
        return new(ScanStatus.Playable, media, crashed, issues, unknown, manifest, newest);
    }

    /// <summary>Group sorted, distinct numbers into runs (a gap starts a new run).</summary>
    public static IReadOnlyList<SegmentRun> Runs(IReadOnlyList<int> sorted)
    {
        var runs = new List<SegmentRun>();
        if (sorted.Count == 0) return runs;
        var first = sorted[0];
        var prev = first;
        for (var i = 1; i < sorted.Count; i++)
        {
            if (sorted[i] == prev + 1) { prev = sorted[i]; continue; }
            runs.Add(new(first, prev));
            first = prev = sorted[i];
        }
        runs.Add(new(first, prev));
        return runs;
    }

    /// <summary>True for <c>bg_*</c> folder names (Steam's rolling cache). Note a clip's inner <c>video/bg_*</c> folder is not temporary.</summary>
    public static bool IsCacheSession(string folder) =>
        FolderNames.ParseRecording(Path.GetFileName(folder.TrimEnd('/', '\\'))) is { Kind: RecordingKind.CacheSession };

    private static void Report(List<ScanIssue> issues, string file, int chunk, HeadCheck h)
    {
        var kind = h switch
        {
            HeadCheck.ZeroFilled => ScanIssueKind.ZeroFilledChunk,
            HeadCheck.TooShort or HeadCheck.NotABox => ScanIssueKind.InvalidChunkHead,
            HeadCheck.Vanished => ScanIssueKind.VanishedChunk,
            HeadCheck.Unreadable => ScanIssueKind.UnreadableChunk,
            _ => (ScanIssueKind?)null,
        };
        if (kind is { } k) issues.Add(new(k, file, chunk));
    }
}
