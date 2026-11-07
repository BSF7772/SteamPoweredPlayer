// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Playback;

/// <summary>
/// A–B loop state and the jump decision. The loop is done by the app (a reload to A when the playhead reaches B), not by mpv's
/// <c>ab-loop-a/b</c>: mpv would jump with an internal seek, and a seek into an already opened chunk breaks decode (plan §3 item 3, S2).
/// Times are media seconds. Not thread-safe (UI thread only).
/// </summary>
public sealed class AbLoop
{
    public double? A { get; private set; }
    public double? B { get; private set; }

    public bool Active => A is { } a && B is { } b && b > a;

    public void SetA(double seconds)
    {
        A = seconds;
        if (B is { } b && b <= seconds) B = null;
    }

    /// <summary>False (and unchanged) when <paramref name="seconds"/> is not after A. Without an A, B is refused too.</summary>
    public bool SetB(double seconds)
    {
        if (A is not { } a || seconds <= a + 0.1) return false;
        B = seconds;
        return true;
    }

    public void Clear()
    {
        A = null;
        B = null;
    }

    /// <summary>The A–B button: sets A, then B, then clears. Returns what happened for the toast text.</summary>
    public string Cycle(double positionSeconds)
    {
        if (A is null) { SetA(positionSeconds); return "A set"; }
        if (B is null) return SetB(positionSeconds) ? "Looping A–B" : "B must be after A";
        Clear();
        return "Loop cleared";
    }

    /// <summary>True when the playhead reached B and must jump back to <see cref="A"/>.</summary>
    public bool ShouldJump(double positionSeconds) => Active && positionSeconds >= B!.Value;
}
