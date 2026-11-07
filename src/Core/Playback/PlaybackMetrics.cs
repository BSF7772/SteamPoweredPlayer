// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Playback;

public enum MetricKind { Open, Seek }

/// <summary>One measured latency: request → mpv's <c>playback-restart</c> (the first frame is ready; the renderer presents it at most one vsync later).</summary>
public sealed record MetricSample(MetricKind Kind, double Milliseconds, DateTimeOffset AtUtc);

public sealed record MetricSummary(int Count, double P50, double P95, double Max);

/// <summary>
/// Open → first frame and seek → first frame (plan §5 budgets: 250 ms / 150 ms). The player calls <see cref="Begin"/> when it issues
/// the request and <see cref="Complete"/> from mpv's <c>PlaybackRestart</c> event; <c>--bench</c> reuses the same samples.
/// Thread-safe. When a request is already pending, the earliest start is kept (a burst of seeks is one measurement, from its first
/// request), except that an open always replaces a pending seek.
/// </summary>
public sealed class PlaybackMetrics
{
    private readonly TimeProvider _time;
    private readonly int _capacity;
    private readonly object _gate = new();
    private readonly List<MetricSample> _samples = [];
    private (MetricKind Kind, long Start)? _pending;

    public PlaybackMetrics(TimeProvider? time = null, int capacity = 200)
    {
        _time = time ?? TimeProvider.System;
        _capacity = Math.Max(1, capacity);
    }

    /// <summary>Raised (on the caller's thread of <see cref="Complete"/>, i.e. mpv's event thread) for every sample.</summary>
    public event Action<MetricSample>? Measured;

    public void Begin(MetricKind kind)
    {
        lock (_gate)
        {
            if (_pending is { } p && !(kind == MetricKind.Open && p.Kind == MetricKind.Seek)) return;
            _pending = (kind, _time.GetTimestamp());
        }
    }

    /// <summary>Drops a pending request (the load failed or was superseded for good).</summary>
    public void Cancel()
    {
        lock (_gate) _pending = null;
    }

    /// <summary>mpv delivered the first frame after the pending request. Null when nothing was pending.</summary>
    public MetricSample? Complete()
    {
        MetricSample sample;
        lock (_gate)
        {
            if (_pending is not { } p) return null;
            _pending = null;
            sample = new MetricSample(p.Kind, _time.GetElapsedTime(p.Start, _time.GetTimestamp()).TotalMilliseconds, _time.GetUtcNow());
            _samples.Add(sample);
            if (_samples.Count > _capacity) _samples.RemoveAt(0);
        }
        try { Measured?.Invoke(sample); } catch (Exception) { /* subscriber bug */ }
        return sample;
    }

    public IReadOnlyList<MetricSample> Samples
    {
        get { lock (_gate) return [.. _samples]; }
    }

    public MetricSample? Last(MetricKind kind)
    {
        lock (_gate) return _samples.LastOrDefault(s => s.Kind == kind);
    }

    public MetricSummary Summary(MetricKind kind)
    {
        double[] ms;
        lock (_gate) ms = [.. _samples.Where(s => s.Kind == kind).Select(s => s.Milliseconds).Order()];
        if (ms.Length == 0) return new(0, 0, 0, 0);
        double Pct(double q) => ms[Math.Min(ms.Length - 1, (int)(q * ms.Length))];
        return new(ms.Length, Pct(0.5), Pct(0.95), ms[^1]);
    }
}
