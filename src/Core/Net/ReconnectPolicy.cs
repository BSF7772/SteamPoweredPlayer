// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Net;

public enum ReconnectState
{
    Connected,
    /// <summary>The link dropped; attempts continue with backoff until <see cref="ReconnectPolicy.Timeout"/>.</summary>
    Reconnecting,
    /// <summary>The server was not reached for <see cref="ReconnectPolicy.Timeout"/>; see <see cref="ReconnectPolicy.Action"/>.</summary>
    TimedOut,
}

/// <summary>What the caller does after a timeout.</summary>
public enum TimedOutAction
{
    /// <summary>Not timed out.</summary>
    None,
    /// <summary>Another server profile exists (LAN → VPN): call <see cref="ReconnectPolicy.TryNextProfile"/>.</summary>
    TryNextProfile,
    /// <summary>Last profile exhausted: show "Can't reach &lt;profile&gt;" with Retry; resumes on the next network change.</summary>
    CannotReach,
}

/// <summary>
/// Pure state machine for reconnecting to a remote library (plan §4.3 "Disconnects and network changes"). No I/O, no timers, no
/// polling: the caller reports events (<see cref="OnDisconnected"/>, <see cref="OnAttemptFailed"/>, <see cref="OnConnected"/>,
/// <see cref="OnNetworkChanged"/>) and schedules its own wake-up from <see cref="TimeUntilNextAttempt"/> and
/// <see cref="TimeUntilTimeout"/>. Time comes from the injected <see cref="TimeProvider"/>.
/// <para>Rules:</para>
/// <list type="bullet">
/// <item>Each profile gets <see cref="Timeout"/> (30 s) of continuous failure; then <see cref="TimedOutAction.TryNextProfile"/> if
/// another profile exists, else <see cref="TimedOutAction.CannotReach"/>.</item>
/// <item>Backoff before attempt k+1 (after k failures): <c>min(<see cref="BaseBackoff"/> · 2^(k−1), <see cref="MaxBackoff"/>)</c>
/// scaled by a jitter factor in [0.5, 1) drawn from the injected <see cref="Random"/>; the first attempt is immediate.</item>
/// <item>A network-change signal (Wi-Fi → other Wi-Fi, a network became available) while reconnecting or timed out retries
/// immediately, resets the backoff, <b>restarts the 30 s clock</b> and returns to the first profile (a new network may reach the
/// LAN again). While connected it is ignored: a broken transport reports itself through <see cref="OnDisconnected"/>.</item>
/// <item>A second drop report while already reconnecting changes nothing (the 30 s clock keeps running).</item>
/// </list>
/// Not thread-safe: drive it from one context.
/// </summary>
public sealed class ReconnectPolicy
{
    public static readonly TimeSpan Timeout = TimeSpan.FromSeconds(30);
    public static readonly TimeSpan BaseBackoff = TimeSpan.FromSeconds(1);
    public static readonly TimeSpan MaxBackoff = TimeSpan.FromSeconds(10);

    private readonly TimeProvider _time;
    private readonly Random _random;
    private readonly int _profileCount;

    private ReconnectState _state = ReconnectState.Connected;
    private long _outageStart;
    private long _nextAttemptAt;
    private int _failures;
    private TimeSpan _delay;

    /// <param name="profileCount">Number of server profiles in failover order (0 = LAN, 1 = VPN, …); at least 1.</param>
    public ReconnectPolicy(TimeProvider time, Random random, int profileCount = 1)
    {
        ArgumentNullException.ThrowIfNull(time);
        ArgumentNullException.ThrowIfNull(random);
        ArgumentOutOfRangeException.ThrowIfLessThan(profileCount, 1);
        _time = time;
        _random = random;
        _profileCount = profileCount;
    }

    /// <summary>Index of the profile to use now.</summary>
    public int ProfileIndex { get; private set; }

    /// <summary>The state; evaluating it applies a pending timeout.</summary>
    public ReconnectState State
    {
        get
        {
            Refresh();
            return _state;
        }
    }

    public TimedOutAction Action
    {
        get
        {
            if (State != ReconnectState.TimedOut) return TimedOutAction.None;
            return ProfileIndex + 1 < _profileCount ? TimedOutAction.TryNextProfile : TimedOutAction.CannotReach;
        }
    }

    /// <summary>The backoff chosen after the latest failed attempt (zero before any failure).</summary>
    public TimeSpan CurrentDelay => _delay;

    /// <summary>How long until the next attempt may start (zero = now). Meaningful while reconnecting.</summary>
    public TimeSpan TimeUntilNextAttempt => Remaining(_nextAttemptAt);

    /// <summary>How long until the current profile times out (zero when already timed out). Meaningful while reconnecting.</summary>
    public TimeSpan TimeUntilTimeout =>
        _state == ReconnectState.Connected ? Timeout : Remaining(_outageStart + Ticks(Timeout));

    /// <summary>The link dropped. Starts the outage clock unless one is already running.</summary>
    public void OnDisconnected()
    {
        Refresh();
        if (_state != ReconnectState.Connected) return;
        BeginOutage();
    }

    /// <summary>A reconnect attempt failed: schedule the next one, or time out when the 30 s are used up.</summary>
    public void OnAttemptFailed()
    {
        Refresh();
        if (_state != ReconnectState.Reconnecting) return;
        _failures++;
        var exp = Math.Min(BaseBackoff.TotalSeconds * Math.Pow(2, _failures - 1), MaxBackoff.TotalSeconds);
        _delay = TimeSpan.FromSeconds(exp * (0.5 + 0.5 * _random.NextDouble()));
        _nextAttemptAt = _time.GetTimestamp() + Ticks(_delay);
        Refresh();
    }

    /// <summary>The server answered (also valid after a timeout, for an attempt that was still in flight). Keeps the profile.</summary>
    public void OnConnected()
    {
        _state = ReconnectState.Connected;
        _failures = 0;
        _delay = TimeSpan.Zero;
    }

    /// <summary>A network became available or changed. See the class remarks.</summary>
    public void OnNetworkChanged()
    {
        Refresh();
        if (_state == ReconnectState.Connected) return;
        ProfileIndex = 0;
        BeginOutage();
    }

    /// <summary>After <see cref="TimedOutAction.TryNextProfile"/>: switch to the next profile with a fresh backoff and 30 s clock.</summary>
    public void TryNextProfile()
    {
        if (Action != TimedOutAction.TryNextProfile) return;
        ProfileIndex++;
        BeginOutage();
    }

    private void BeginOutage()
    {
        _state = ReconnectState.Reconnecting;
        _outageStart = _time.GetTimestamp();
        _nextAttemptAt = _outageStart;
        _failures = 0;
        _delay = TimeSpan.Zero;
    }

    private void Refresh()
    {
        if (_state == ReconnectState.Reconnecting && _time.GetTimestamp() - _outageStart >= Ticks(Timeout))
            _state = ReconnectState.TimedOut;
    }

    private long Ticks(TimeSpan span) => (long)(span.TotalSeconds * _time.TimestampFrequency);

    private TimeSpan Remaining(long deadline)
    {
        var left = deadline - _time.GetTimestamp();
        return left <= 0 ? TimeSpan.Zero : TimeSpan.FromSeconds((double)left / _time.TimestampFrequency);
    }
}
