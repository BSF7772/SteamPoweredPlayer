// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Tests.Fakes;

/// <summary>A clock whose timers fire only when <see cref="Advance"/> passes their due time (callbacks run on the calling thread).</summary>
public sealed class ManualTimeProvider : TimeProvider
{
    private readonly object _lock = new();
    private readonly List<ManualTimer> _timers = [];
    private long _ticks;

    public DateTimeOffset Start { get; } = new(2026, 1, 1, 12, 0, 0, TimeSpan.Zero);

    public override DateTimeOffset GetUtcNow() => Start + TimeSpan.FromTicks(Volatile.Read(ref _ticks));
    public override long GetTimestamp() => Volatile.Read(ref _ticks);
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        var t = new ManualTimer(this, callback, state);
        lock (_lock) _timers.Add(t);
        t.Change(dueTime, period);
        return t;
    }

    public void Advance(TimeSpan by)
    {
        var target = Volatile.Read(ref _ticks) + by.Ticks;
        while (true)
        {
            ManualTimer? next;
            lock (_lock) next = _timers.Where(t => t.Due is { } d && d <= target).OrderBy(t => t.Due).FirstOrDefault();
            if (next is null) break;
            Volatile.Write(ref _ticks, Math.Max(Volatile.Read(ref _ticks), next.Due!.Value));
            next.Fire();
        }
        Volatile.Write(ref _ticks, target);
    }

    private sealed class ManualTimer(ManualTimeProvider owner, TimerCallback cb, object? state) : ITimer
    {
        public long? Due { get; private set; }
        private long _period;

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (owner._lock)
            {
                Due = dueTime == Timeout.InfiniteTimeSpan ? null : Volatile.Read(ref owner._ticks) + dueTime.Ticks;
                _period = period == Timeout.InfiniteTimeSpan ? 0 : period.Ticks;
            }
            return true;
        }

        public void Fire()
        {
            lock (owner._lock) Due = _period > 0 ? Due + _period : null;
            cb(state);
        }

        public void Dispose() { lock (owner._lock) { Due = null; owner._timers.Remove(this); } }
        public ValueTask DisposeAsync() { Dispose(); return ValueTask.CompletedTask; }
    }
}
