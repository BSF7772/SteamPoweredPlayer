// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Core.Playback;

public enum FollowAction
{
    /// <summary>Keep playing; the new index only replaces the old one.</summary>
    None,
    /// <summary>The playhead is in a region Steam removed: jump to the new head.</summary>
    JumpToHead,
    /// <summary>Everything was removed: nothing to jump to.</summary>
    Emptied,
}

/// <param name="HeadRemovedSeconds">How much Steam trimmed off the head in this update (0 when nothing).</param>
/// <param name="JumpedFromSeconds">Where the playhead was when it was lost (media time).</param>
public sealed record FollowDecision(FollowAction Action, double TargetSeconds, double HeadRemovedSeconds, double JumpedFromSeconds)
{
    /// <summary>Seconds ahead of the playhead that disappeared (new head − old position): the "Steam removed X s" figure for the user.</summary>
    public double LostAtPlayheadSeconds => Action == FollowAction.JumpToHead ? TargetSeconds - JumpedFromSeconds : 0;
}

public enum EndAction
{
    /// <summary>More chunks exist beyond the window: rebuild and continue now.</summary>
    Continue,
    /// <summary>Live session at its head: wait until it grows.</summary>
    WaitForGrowth,
    /// <summary>The recording ended.</summary>
    Finished,
}

/// <summary>Reported by <see cref="CacheFollower.Trimmed"/> when Steam's trim took the playhead's region.</summary>
public sealed record CacheTrim(double HeadRemovedSeconds, double FromSeconds, double ToSeconds, bool Emptied)
{
    /// <summary>Seconds of the user's upcoming media that vanished ("Steam removed X s").</summary>
    public double RemovedSeconds => Emptied ? HeadRemovedSeconds : ToSeconds - FromSeconds;
}

/// <summary>
/// Keeps a <see cref="PlaybackSession"/> on a moving recording: the caller (e.g. a FileSystemWatcher-triggered rescan) passes the new
/// <see cref="SessionMedia"/> to <see cref="OnMediaChangedAsync"/>, and EOF at the end of the window continues into new chunks
/// (S2: EOF → rebuilt EDL → <c>loadfile start=</c> continues within 0.03 s). The decisions are pure static functions.
/// </summary>
public sealed class CacheFollower : IDisposable
{
    private readonly PlaybackSession _session;

    public CacheFollower(PlaybackSession session)
    {
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _session.ReachedEnd += OnReachedEnd;
    }

    /// <summary>Steam removed the playhead's region: the position jumped to the new head.</summary>
    public event Action<CacheTrim>? Trimmed;

    /// <summary>A new index of the same recording. Applies it, then jumps, continues after a live EOF, or does nothing.</summary>
    public async Task OnMediaChangedAsync(SessionMedia updated, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(updated);
        var old = _session.Media;
        var position = _session.Position;
        var window = _session.Window;
        var atEnd = _session.AtEnd;
        var userPaused = _session.UserPaused;
        _session.ReplaceMedia(updated);
        if (old is null) return;

        var d = DecideOnMediaChange(old, updated, position);
        switch (d.Action)
        {
            case FollowAction.JumpToHead:
                Trimmed?.Invoke(new CacheTrim(d.HeadRemovedSeconds, d.JumpedFromSeconds, d.TargetSeconds, false));
                await _session.ReloadAsync(d.TargetSeconds, ReloadCause.CacheChange, true, cancellationToken).ConfigureAwait(false);
                return;
            case FollowAction.Emptied:
                Trimmed?.Invoke(new CacheTrim(d.HeadRemovedSeconds, d.JumpedFromSeconds, d.JumpedFromSeconds, true));
                return;
        }

        // The playhead sat at the end of a live window and the session has grown since: continue.
        if (atEnd && !userPaused && window.Count > 0 && DecideAtEnd(updated, window) == EndAction.Continue)
            await _session.ReloadAsync(ResumePosition(window, updated, position), ReloadCause.EndOfWindow, true, cancellationToken).ConfigureAwait(false);
    }

    private void OnReachedEnd(double position)
    {
        var media = _session.Media;
        var window = _session.Window;
        if (media is null || window.Count == 0) return;
        if (DecideAtEnd(media, window) != EndAction.Continue) return;     // WaitForGrowth: OnMediaChangedAsync resumes it
        _ = ContinueAsync(ResumePosition(window, media, position));
    }

    private async Task ContinueAsync(double position)
    {
        try { await _session.ReloadAsync(position, ReloadCause.EndOfWindow, true).ConfigureAwait(false); }
        catch (Exception) { /* surfaced through PlaybackSession.ReloadFailed */ }
    }

    // ---- pure decisions ------------------------------------------------------------------------------------------

    /// <summary>Is the playhead still inside what is on disk? Steam trims the oldest end, so only a head trim can take it.</summary>
    public static FollowDecision DecideOnMediaChange(SessionMedia old, SessionMedia updated, double positionSeconds)
    {
        ArgumentNullException.ThrowIfNull(old);
        ArgumentNullException.ThrowIfNull(updated);
        var oldHead = old.FirstChunk is { } of ? old.ChunkStartSeconds(of) : 0;
        if (updated.FirstChunk is not { } first) return new(FollowAction.Emptied, 0, old.ChunkCount * old.SegmentSeconds, positionSeconds);
        var head = updated.ChunkStartSeconds(first);
        var removed = Math.Max(0, head - oldHead);
        if (!double.IsNaN(positionSeconds) && positionSeconds < head)
            return new(FollowAction.JumpToHead, head, removed, positionSeconds);
        return new(FollowAction.None, positionSeconds, removed, positionSeconds);
    }

    /// <summary>What to do when mpv hits EOF of a window whose chunks are <paramref name="window"/>.</summary>
    public static EndAction DecideAtEnd(SessionMedia media, IReadOnlyList<int> window)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(window);
        if (window.Count > 0 && media.LastChunk is { } last && last > window[^1]) return EndAction.Continue;
        return media.Live ? EndAction.WaitForGrowth : EndAction.Finished;
    }

    /// <summary>Where to restart after the window's end: not before the window's last chunk ends (no replay of the last frames).</summary>
    private static double ResumePosition(IReadOnlyList<int> window, SessionMedia media, double position)
    {
        var windowEnd = window[^1] * media.SegmentSeconds;
        return double.IsNaN(position) ? windowEnd : Math.Max(position, windowEnd);
    }

    public void Dispose() => _session.ReachedEnd -= OnReachedEnd;
}
