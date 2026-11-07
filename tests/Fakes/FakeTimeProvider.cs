// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Tests.Fakes;

/// <summary>A manually advanced clock (the real one is never consulted). Timestamps tick at 1 per 100 ns.</summary>
public sealed class FakeTimeProvider : TimeProvider
{
    private long _ticks;
    private readonly DateTimeOffset _start = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public void Advance(TimeSpan by) => _ticks += by.Ticks;

    public override DateTimeOffset GetUtcNow() => _start + TimeSpan.FromTicks(_ticks);
    public override long GetTimestamp() => _ticks;
    public override long TimestampFrequency => TimeSpan.TicksPerSecond;
}
