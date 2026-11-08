// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Playback;

/// <summary>What the mpv instance draws on.</summary>
public enum MpvOutput
{
    /// <summary><c>vo=libmpv</c>: frames go to <see cref="MpvD3D11Renderer"/> (create the renderer before the first load).</summary>
    Render,
    /// <summary><c>vo=null</c>, <c>ao=null</c>: decodes and keeps time, shows nothing (tests, trickplay probes).</summary>
    Headless,
}

/// <summary><c>mpv_end_file_reason</c>.</summary>
public enum MpvEndReason { Eof, Stop, Quit, Error, Redirect }

/// <summary>An <c>MPV_EVENT_END_FILE</c>. <see cref="ErrorText"/> is set for <see cref="MpvEndReason.Error"/>.</summary>
public sealed record MpvEndFile(MpvEndReason Reason, int ErrorCode, string? ErrorText);

/// <summary>An mpv log line (<c>Level</c> is mpv's name: fatal, error, warn, info, v, debug, trace).</summary>
public sealed record MpvLogMessage(string Prefix, string Level, string Text);

/// <summary>An mpv call failed (the message carries mpv's error string).</summary>
public sealed class MpvException(string message, int errorCode = 0) : Exception(message)
{
    public int ErrorCode { get; } = errorCode;
}

/// <summary>
/// What <see cref="PlaybackSession"/> needs from mpv. <see cref="MpvPlayer"/> is the real one; tests use a fake. Events are raised on
/// the player's own thread (never the UI thread) and must not block.
/// </summary>
public interface IMpvPlayer : IAsyncDisposable
{
    /// <summary>mpv's <c>time-pos</c> in seconds (media time with the lead EDL), <c>NaN</c> while nothing is loaded.</summary>
    double TimePos { get; }

    /// <summary>mpv's <c>eof-reached</c>: with <c>keep-open=yes</c> mpv pauses on the last frame and this stays true until the next load.</summary>
    bool EofReached { get; }

    event Action<double>? TimePosChanged;
    event Action<bool>? EofReachedChanged;
    event Action<MpvEndFile>? EndFile;

    /// <summary>
    /// <c>loadfile &lt;edl&gt; replace -1 start=&lt;t&gt;[,end=&lt;e&gt;]</c>; <paramref name="endSeconds"/> (media time) makes mpv end the file there, as a
    /// saved clip needs (<see cref="Library.SessionMedia.EndLimitSeconds"/>: the EDL's own part lengths don't cut anything). <c>pause</c> survives a load in mpv, so it is always set explicitly first:
    /// <paramref name="play"/> false loads paused. Completes with true when the first frame (playback-restart) arrives,
    /// false on timeout or when a newer load replaced this one; throws <see cref="MpvException"/> when mpv cannot open the EDL.
    /// </summary>
    Task<bool> LoadAsync(string edl, double startSeconds, bool play, double? endSeconds = null, CancellationToken cancellationToken = default);

    void SetPause(bool paused);
    void SetSpeed(double speed);
    void SetVolume(double percent);
    void SetMute(bool muted);
    void SetAudioDelay(double seconds);

    /// <summary>Sizes mpv's demuxer cache for the next load (decision D-RAM, <see cref="DemuxerCache"/>). Fakes may ignore it.</summary>
    void SetDemuxerCache(DemuxerCachePlan plan) { }

    /// <summary>Forward single-frame step (pauses). Stepping back across a chunk is a reload decided by the caller.</summary>
    void FrameStep();

    /// <summary>Loops between a and b media seconds; <c>null, null</c> clears the loop.</summary>
    void SetAbLoop(double? a, double? b);

    Task ScreenshotToFileAsync(string path, CancellationToken cancellationToken = default);
}
