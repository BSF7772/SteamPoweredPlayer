// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Tests.Fakes;

/// <summary>An <see cref="IMpvPlayer"/> that records loads. Loads complete at once unless <see cref="Gate"/> is set.</summary>
public sealed class FakeMpvPlayer : IMpvPlayer
{
    public sealed record Load(string Edl, double Start, bool Play, double? End = null);

    public List<Load> Loads { get; } = [];
    public bool Paused { get; private set; } = true;
    public double Speed { get; private set; } = 1;
    public int FrameSteps { get; private set; }
    public double TimePos { get; private set; } = double.NaN;
    public bool EofReached { get; private set; }

    /// <summary>When set, <see cref="LoadAsync"/> records the call and then waits for this task.</summary>
    public TaskCompletionSource? Gate { get; set; }

    public event Action<double>? TimePosChanged;
    public event Action<bool>? EofReachedChanged;
    public event Action<MpvEndFile>? EndFile;

    public async Task<bool> LoadAsync(string edl, double startSeconds, bool play, double? endSeconds = null, CancellationToken cancellationToken = default)
    {
        lock (Loads) Loads.Add(new Load(edl, startSeconds, play, endSeconds));
        Paused = !play;
        var gate = Gate;
        if (gate is not null) await gate.Task.WaitAsync(cancellationToken);
        TimePos = startSeconds;
        if (FramePts is { Length: > 0 } pts)
        {
            // like mpv's start seek: frames before the start are dropped, the first frame at/after it is shown (the last one past the end)
            _index = 0;
            while (_index + 1 < pts.Length && pts[_index] < startSeconds) _index++;
            TimePos = pts[_index];
        }
        if (EofReached) { EofReached = false; EofReachedChanged?.Invoke(false); }
        return true;
    }

    /// <summary>Presentation times of the frames (ascending). When set, loads show the frame at/before the start and frame steps move through them.</summary>
    public double[]? FramePts { get; set; }
    private int _index;

    /// <summary>This many coming frame steps report no new time (a decode too slow for the probe's timeout).</summary>
    public int SilentSteps { get; set; }

    public int LoadCount { get { lock (Loads) return Loads.Count; } }

    public void Tick(double t) { TimePos = t; TimePosChanged?.Invoke(t); }

    public void Eof(double t)
    {
        TimePos = t;
        EofReached = true;
        Paused = true;
        EofReachedChanged?.Invoke(true);
    }

    public int PauseCalls { get; private set; }
    public void SetPause(bool paused) { PauseCalls++; Paused = paused; }
    public void SetSpeed(double speed) => Speed = speed;
    public void SetVolume(double percent) { }
    public void SetMute(bool muted) { }
    public void SetAudioDelay(double seconds) { }
    public DemuxerCachePlan? DemuxerCache { get; private set; }
    public void SetDemuxerCache(DemuxerCachePlan plan) => DemuxerCache = plan;
    public void FrameStep()
    {
        FrameSteps++;
        Paused = true;
        if (FramePts is not { Length: > 0 } pts || _index + 1 >= pts.Length) return;
        if (SilentSteps > 0) { SilentSteps--; return; }
        _index++;
        Tick(pts[_index]);
    }
    public (double? A, double? B) AbLoop { get; private set; }
    public void SetAbLoop(double? a, double? b) => AbLoop = (a, b);
    public Task ScreenshotToFileAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    internal void Unused() => EndFile?.Invoke(new MpvEndFile(MpvEndReason.Stop, 0, null));
}
