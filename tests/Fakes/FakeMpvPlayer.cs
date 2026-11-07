// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Playback;

namespace SteamPoweredPlayer.Tests.Fakes;

/// <summary>An <see cref="IMpvPlayer"/> that records loads. Loads complete at once unless <see cref="Gate"/> is set.</summary>
public sealed class FakeMpvPlayer : IMpvPlayer
{
    public sealed record Load(string Edl, double Start, bool Play);

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

    public async Task<bool> LoadAsync(string edl, double startSeconds, bool play, CancellationToken cancellationToken = default)
    {
        lock (Loads) Loads.Add(new Load(edl, startSeconds, play));
        Paused = !play;
        var gate = Gate;
        if (gate is not null) await gate.Task.WaitAsync(cancellationToken);
        TimePos = startSeconds;
        if (EofReached) { EofReached = false; EofReachedChanged?.Invoke(false); }
        return true;
    }

    public int LoadCount { get { lock (Loads) return Loads.Count; } }

    public void Tick(double t) { TimePos = t; TimePosChanged?.Invoke(t); }

    public void Eof(double t)
    {
        TimePos = t;
        EofReached = true;
        Paused = true;
        EofReachedChanged?.Invoke(true);
    }

    public void SetPause(bool paused) => Paused = paused;
    public void SetSpeed(double speed) => Speed = speed;
    public void SetVolume(double percent) { }
    public void SetMute(bool muted) { }
    public void SetAudioDelay(double seconds) { }
    public DemuxerCachePlan? DemuxerCache { get; private set; }
    public void SetDemuxerCache(DemuxerCachePlan plan) => DemuxerCache = plan;
    public void FrameStep() { FrameSteps++; Paused = true; }
    public void SetAbLoop(double? a, double? b) { }
    public Task ScreenshotToFileAsync(string path, CancellationToken cancellationToken = default) => Task.CompletedTask;
    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    internal void Unused() => EndFile?.Invoke(new MpvEndFile(MpvEndReason.Stop, 0, null));
}
