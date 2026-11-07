// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Net;
using SteamPoweredPlayer.Tests.Fakes;

namespace SteamPoweredPlayer.Tests.Net;

public sealed class ReconnectPolicyTests
{
    /// <summary>NextDouble always returns the same value, so the jitter is deterministic.</summary>
    private sealed class ConstRandom(double value) : Random
    {
        public override double NextDouble() => value;
    }

    private static TimeSpan S(double seconds) => TimeSpan.FromSeconds(seconds);

    private static (ReconnectPolicy policy, FakeTimeProvider clock) Make(int profiles = 1, double jitter = 1.0)
    {
        var clock = new FakeTimeProvider();
        return (new ReconnectPolicy(clock, new ConstRandom(jitter), profiles), clock);
    }

    [Fact]
    public void Timeout_is_30_seconds() => Assert.Equal(S(30), ReconnectPolicy.Timeout);

    [Fact]
    public void Starts_connected_on_the_first_profile()
    {
        var (p, _) = Make(2);
        Assert.Equal(ReconnectState.Connected, p.State);
        Assert.Equal(0, p.ProfileIndex);
        Assert.Equal(TimedOutAction.None, p.Action);
    }

    [Fact]
    public void Drop_starts_reconnecting_with_an_immediate_first_attempt()
    {
        var (p, _) = Make();
        p.OnDisconnected();
        Assert.Equal(ReconnectState.Reconnecting, p.State);
        Assert.Equal(TimeSpan.Zero, p.TimeUntilNextAttempt);
        Assert.Equal(S(30), p.TimeUntilTimeout);
    }

    [Fact]
    public void Backoff_grows_exponentially_and_is_capped()
    {
        var (p, clock) = Make(jitter: 1.0);           // jitter factor 1.0 = the full exponential delay
        p.OnDisconnected();
        var delays = new List<double>();
        for (var i = 0; i < 4; i++)
        {
            p.OnAttemptFailed();
            delays.Add(p.CurrentDelay.TotalSeconds);
            clock.Advance(p.CurrentDelay);
        }
        Assert.Equal([1.0, 2.0, 4.0, 8.0], delays);

        p.OnAttemptFailed();
        Assert.Equal(ReconnectPolicy.MaxBackoff, p.CurrentDelay);   // 16 s would exceed the cap
    }

    [Theory]
    [InlineData(0.0, 0.5)]
    [InlineData(0.5, 0.75)]
    [InlineData(1.0, 1.0)]
    public void Jitter_scales_the_delay_between_half_and_full(double random, double factor)
    {
        var (p, _) = Make(jitter: random);
        p.OnDisconnected();
        p.OnAttemptFailed();
        Assert.Equal(1.0 * factor, p.CurrentDelay.TotalSeconds, 6);
    }

    [Fact]
    public void Next_attempt_waits_for_the_backoff_delay()
    {
        var (p, clock) = Make();
        p.OnDisconnected();
        p.OnAttemptFailed();                           // 1 s
        clock.Advance(S(0.4));
        Assert.Equal(0.6, p.TimeUntilNextAttempt.TotalSeconds, 6);
        clock.Advance(S(2));
        Assert.Equal(TimeSpan.Zero, p.TimeUntilNextAttempt);
    }

    [Fact]
    public void Outage_of_29_seconds_resumes()
    {
        var (p, clock) = Make();
        p.OnDisconnected();
        clock.Advance(S(29));
        Assert.Equal(ReconnectState.Reconnecting, p.State);
        p.OnConnected();
        Assert.Equal(ReconnectState.Connected, p.State);
        Assert.Equal(TimedOutAction.None, p.Action);
    }

    [Fact]
    public void Outage_of_30_seconds_times_out_with_cannot_reach_when_there_is_one_profile()
    {
        var (p, clock) = Make(1);
        p.OnDisconnected();
        clock.Advance(S(30));
        Assert.Equal(ReconnectState.TimedOut, p.State);
        Assert.Equal(TimedOutAction.CannotReach, p.Action);
        Assert.Equal(TimeSpan.Zero, p.TimeUntilTimeout);
    }

    [Fact]
    public void Timeout_with_another_profile_says_try_next_profile()
    {
        var (p, clock) = Make(2);
        p.OnDisconnected();
        clock.Advance(S(30));
        Assert.Equal(ReconnectState.TimedOut, p.State);
        Assert.Equal(TimedOutAction.TryNextProfile, p.Action);
    }

    [Fact]
    public void Failover_goes_LAN_then_VPN_then_cannot_reach()
    {
        var (p, clock) = Make(2);                      // 0 = LAN, 1 = VPN
        p.OnDisconnected();
        Assert.Equal(0, p.ProfileIndex);

        clock.Advance(S(30));
        Assert.Equal(TimedOutAction.TryNextProfile, p.Action);
        p.TryNextProfile();
        Assert.Equal(1, p.ProfileIndex);
        Assert.Equal(ReconnectState.Reconnecting, p.State);
        Assert.Equal(S(30), p.TimeUntilTimeout);       // the VPN gets its own full 30 s

        clock.Advance(S(30));
        Assert.Equal(ReconnectState.TimedOut, p.State);
        Assert.Equal(TimedOutAction.CannotReach, p.Action);
        Assert.Equal(1, p.ProfileIndex);
    }

    [Fact]
    public void TryNextProfile_outside_a_try_next_timeout_is_a_no_op()
    {
        var (p, _) = Make(2);
        p.TryNextProfile();
        Assert.Equal(0, p.ProfileIndex);
        Assert.Equal(ReconnectState.Connected, p.State);
    }

    [Fact]
    public void Network_change_resumes_immediately_resets_the_backoff_and_restarts_the_30_second_clock()
    {
        var (p, clock) = Make();
        p.OnDisconnected();
        for (var i = 0; i < 3; i++) { p.OnAttemptFailed(); clock.Advance(p.CurrentDelay); }
        clock.Advance(S(20));                          // 27 s into the outage with a long backoff
        Assert.True(p.TimeUntilTimeout < S(10));

        p.OnNetworkChanged();
        Assert.Equal(ReconnectState.Reconnecting, p.State);
        Assert.Equal(TimeSpan.Zero, p.TimeUntilNextAttempt);   // retry now
        Assert.Equal(S(30), p.TimeUntilTimeout);                // the clock restarted

        p.OnAttemptFailed();                                    // backoff starts over at the first step
        Assert.Equal(1.0, p.CurrentDelay.TotalSeconds);

        clock.Advance(S(29));                                   // would have timed out without the restart
        Assert.Equal(ReconnectState.Reconnecting, p.State);
        clock.Advance(S(1));
        Assert.Equal(ReconnectState.TimedOut, p.State);
    }

    [Fact]
    public void Network_change_returns_to_the_first_profile()
    {
        var (p, clock) = Make(2);
        p.OnDisconnected();
        clock.Advance(S(30));
        p.TryNextProfile();                            // on the VPN now
        p.OnNetworkChanged();                          // maybe back on the home Wi-Fi: LAN first again
        Assert.Equal(0, p.ProfileIndex);
        Assert.Equal(ReconnectState.Reconnecting, p.State);
    }

    [Fact]
    public void Cannot_reach_resumes_on_the_next_network_change_only()
    {
        var (p, clock) = Make(1);
        p.OnDisconnected();
        clock.Advance(S(30));
        Assert.Equal(TimedOutAction.CannotReach, p.Action);

        clock.Advance(S(600));
        Assert.Equal(ReconnectState.TimedOut, p.State);          // no timer revives it
        p.OnAttemptFailed();                                     // stray result of an in-flight attempt: ignored
        Assert.Equal(ReconnectState.TimedOut, p.State);

        p.OnNetworkChanged();
        Assert.Equal(ReconnectState.Reconnecting, p.State);
        Assert.Equal(TimeSpan.Zero, p.TimeUntilNextAttempt);
        Assert.Equal(S(30), p.TimeUntilTimeout);
        Assert.Equal(TimedOutAction.None, p.Action);
    }

    [Fact]
    public void Network_change_while_connected_does_nothing()
    {
        var (p, _) = Make(2);
        p.OnNetworkChanged();
        Assert.Equal(ReconnectState.Connected, p.State);
        Assert.Equal(0, p.ProfileIndex);
    }

    [Fact]
    public void Success_after_a_timeout_connects_and_resets()
    {
        var (p, clock) = Make(2);
        p.OnDisconnected();
        clock.Advance(S(30));
        p.TryNextProfile();
        p.OnConnected();
        Assert.Equal(ReconnectState.Connected, p.State);
        Assert.Equal(1, p.ProfileIndex);                         // stays on the profile that worked

        p.OnDisconnected();                                      // a later drop: fresh backoff and clock
        p.OnAttemptFailed();
        Assert.Equal(1.0, p.CurrentDelay.TotalSeconds);
        Assert.Equal(S(30), p.TimeUntilTimeout);
    }

    [Fact]
    public void A_second_drop_while_reconnecting_does_not_restart_the_clock()
    {
        var (p, clock) = Make();
        p.OnDisconnected();
        clock.Advance(S(20));
        p.OnDisconnected();
        Assert.Equal(S(10), p.TimeUntilTimeout);
    }

    [Fact]
    public void Failed_attempt_after_the_deadline_times_out()
    {
        var (p, clock) = Make(1);
        p.OnDisconnected();
        clock.Advance(S(31));
        p.OnAttemptFailed();
        Assert.Equal(ReconnectState.TimedOut, p.State);
    }

    [Fact]
    public void Invalid_profile_count_is_rejected() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => new ReconnectPolicy(new FakeTimeProvider(), new Random(1), 0));
}
