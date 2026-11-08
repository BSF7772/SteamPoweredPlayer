// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Net;

/// <summary>What the OS says a connection costs (mirrors Windows' <c>NetworkCostType</c>; the app maps the OS value to this).</summary>
public enum ConnectionCost { Unknown, Unrestricted, Fixed, Variable }

/// <summary>
/// The only thing the app does about metered networks (plan §4.3): when a transfer starts on one, say so once, with "Don't show again".
/// No cap, no prompt, no pause. Pure decision logic; the notice itself is UI.
/// </summary>
public sealed class MeteredNotice
{
    private bool _shown;

    /// <summary>A fixed data plan or a per-use cost. Unknown and unrestricted connections count as free.</summary>
    public static bool IsMetered(ConnectionCost cost) => cost is ConnectionCost.Fixed or ConnectionCost.Variable;

    /// <summary>
    /// Whether to show the notice for a transfer starting now: on a metered connection, not dismissed for good, and not already shown
    /// in this run of the app. A true answer counts as shown.
    /// </summary>
    public bool ShouldShow(ConnectionCost cost, bool dismissedForGood)
    {
        if (dismissedForGood || _shown || !IsMetered(cost)) return false;
        _shown = true;
        return true;
    }
}
