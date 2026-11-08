// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Net;

namespace SteamPoweredPlayer.Tests.Net;

public sealed class MeteredNoticeTests
{
    [Theory]
    [InlineData(ConnectionCost.Fixed, true)]
    [InlineData(ConnectionCost.Variable, true)]
    [InlineData(ConnectionCost.Unrestricted, false)]
    [InlineData(ConnectionCost.Unknown, false)]
    public void Only_fixed_and_variable_costs_are_metered(ConnectionCost cost, bool metered) =>
        Assert.Equal(metered, MeteredNotice.IsMetered(cost));

    [Fact]
    public void Shows_once_on_a_metered_network()
    {
        var notice = new MeteredNotice();
        Assert.True(notice.ShouldShow(ConnectionCost.Variable, dismissedForGood: false));
        Assert.False(notice.ShouldShow(ConnectionCost.Variable, dismissedForGood: false));
        Assert.False(notice.ShouldShow(ConnectionCost.Fixed, dismissedForGood: false));
    }

    [Fact]
    public void An_unmetered_start_does_not_use_up_the_notice()
    {
        var notice = new MeteredNotice();
        Assert.False(notice.ShouldShow(ConnectionCost.Unrestricted, dismissedForGood: false));
        Assert.True(notice.ShouldShow(ConnectionCost.Fixed, dismissedForGood: false));
    }

    [Fact]
    public void Dont_show_again_wins()
    {
        var notice = new MeteredNotice();
        Assert.False(notice.ShouldShow(ConnectionCost.Fixed, dismissedForGood: true));
        Assert.True(notice.ShouldShow(ConnectionCost.Fixed, dismissedForGood: false));   // a dismissed flag is the caller's, not remembered here
    }
}
