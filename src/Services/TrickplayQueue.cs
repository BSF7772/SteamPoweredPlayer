// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core;
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Services;

/// <summary>
/// Background trickplay generation (plan Phase 2). After the scan settles it builds sheets for clips and cache sessions, the selected
/// game first and the newest first, one recording at a time at below-normal priority. Only runs with a confirmed data folder.
/// <see cref="Pause"/> cancels the running job (what it produced is committed) and holds the queue until the matching
/// <see cref="Resume"/>; the player calls them around opening a recording. No polling: it wakes on scan-settled / index changes.
/// </summary>
public sealed class TrickplayQueue : IDisposable
{
    private readonly LibraryServiceSource _source;
    private readonly Func<DecodeMode> _mode;
    private readonly Func<string?> _preferredGame;
    private readonly CancellationTokenSource _cts = new();
    private readonly SemaphoreSlim _wake = new(0);
    private readonly object _lock = new();
    private readonly Dictionary<string, (int First, int Last, int Count)> _done = new(StringComparer.Ordinal);   // folder -> chunk range it was completed for
    private readonly HashSet<string> _failed = new(StringComparer.Ordinal);          // gave up for this process (no ffmpeg frames, bad data)
    private int _pauses;
    private int _kickPending;
    private CancellationTokenSource? _run;
    private volatile bool _settled;
    private bool _disposed;

    /// <param name="preferredGame">The game selected in the Library page (its recordings go first); may return null.</param>
    public TrickplayQueue(LibraryServiceSource source, Func<DecodeMode> mode, Func<string?> preferredGame)
    {
        _source = source;
        _mode = mode;
        _preferredGame = preferredGame;
        _source.ScanSettled += (_, _) => { _settled = true; Kick(); };
        _source.IndexChanged += (_, _) => { if (_settled) Kick(); };
        _ = Task.Run(WorkerAsync);
    }

    /// <summary>The folder name that keys a recording's trickplay: the last segment of <c>video/bg_…</c> or <c>clips/clip_…</c>.</summary>
    public static string KeyOf(string relativeFolder) => relativeFolder[(relativeFolder.LastIndexOf('/') + 1)..];

    /// <summary>The recording being worked on now (for a status line), or null.</summary>
    public string? Current { get; private set; }

    /// <summary>Recordings still waiting (approximate, updated per job).</summary>
    public int Remaining { get; private set; }

    public bool IsPaused { get { lock (_lock) return _pauses > 0; } }

    /// <summary>Stops the running job now and holds the queue. Calls nest: every Pause needs one Resume.</summary>
    public void Pause()
    {
        lock (_lock)
        {
            _pauses++;
            _run?.Cancel();
        }
    }

    public void Resume()
    {
        var wake = false;
        lock (_lock)
        {
            if (_pauses > 0 && --_pauses == 0) wake = true;
        }
        if (wake) Kick();
    }

    /// <summary>Asks the worker to look for work (a new game was selected, the data folder was confirmed, …).</summary>
    public void Kick()
    {
        if (_disposed) return;
        if (Interlocked.Exchange(ref _kickPending, 1) == 0) _wake.Release();
    }

    private async Task WorkerAsync()
    {
        var ct = _cts.Token;
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(ct).ConfigureAwait(false);
                Interlocked.Exchange(ref _kickPending, 0);
                await DrainAsync(ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task DrainAsync(CancellationToken ct)
    {
        if (!_settled || !Trickplay.FfmpegAvailable()) return;   // not while the scan still needs the disk (plan §9: cold scan is disk-bound)
        while (!ct.IsCancellationRequested)
        {
            var data = _source.Data;
            if (data is null || IsPaused) break;
            if (NextJob() is not { } job) { Current = null; Remaining = 0; break; }

            using var run = CancellationTokenSource.CreateLinkedTokenSource(ct);
            lock (_lock)
            {
                if (_pauses > 0) break;
                _run = run;
            }
            Current = job.Folder;
            try
            {
                var opts = new TrickplayOptions(Mode: _mode());
                var res = await Trickplay.GenerateAsync(data, KeyOf(job.Folder), job.Media, opts, null, run.Token).ConfigureAwait(false);
                lock (_lock) _done[job.Folder] = SignatureOf(job.Media);
                if (res.SheetsWritten > 0) _source.TrickplayUpdated(job.Folder);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // Paused: the partial sheets are committed; Resume() wakes us and the job continues where it stopped.
                _source.TrickplayUpdated(job.Folder);
                break;
            }
            catch (OperationCanceledException) { break; }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or InvalidOperationException or FileNotFoundException or ArgumentException)
            {
                // A recording Steam is still writing can fail for a moment (a chunk vanishes or is half written): retry at the next chunk, not never.
                lock (_lock)
                {
                    if (job.Media.Live) _done[job.Folder] = SignatureOf(job.Media);
                    else _failed.Add(job.Folder);
                }
            }
            finally
            {
                lock (_lock) _run = null;
            }
        }
        Current = null;
    }

    private sealed record Job(string Folder, SessionMedia Media);

    /// <summary>What a job was done for: the chunk range, so growth at the tail and Steam's trim at the head both count as new work.</summary>
    private static (int First, int Last, int Count) SignatureOf(SessionMedia m) => (m.Runs[0].First, m.Runs[^1].Last, m.ChunkCount);

    /// <summary>Selected game first, then newest first; live sessions wait (they are still growing).</summary>
    private Job? NextJob()
    {
        var index = _source.Current;
        if (index is null) return null;
        var preferred = _preferredGame();
        var todo = new List<(RecordingEntry R, SessionMedia M)>();
        lock (_lock)
        {
            foreach (var r in index.Recordings)
            {
                if (!Trickplay.IsSafeKey(KeyOf(r.RelativeFolder))) continue;
                if (_failed.Contains(r.RelativeFolder)) continue;
                // A live session is built up to its second-newest chunk (the newest may still be growing); every new chunk is one more tile.
                if (_source.GetMedia(r.RelativeFolder) is not { } full || TrickplayLive.Buildable(full) is not { } m) continue;
                if (_done.TryGetValue(r.RelativeFolder, out var n) && n == SignatureOf(m)) continue;
                todo.Add((r, m));
            }
        }
        Remaining = todo.Count;
        if (todo.Count == 0) return null;
        var best = todo
            .OrderBy(t => preferred is not null && t.R.GameId == preferred ? 0 : 1)
            .ThenByDescending(t => t.R.StartUtc)
            .First();
        return new(best.R.RelativeFolder, best.M);
    }

    public void Dispose()
    {
        _disposed = true;
        _cts.Cancel();
        lock (_lock) _run?.Cancel();
    }
}
