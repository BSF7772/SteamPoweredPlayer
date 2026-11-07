// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Services;

/// <summary>
/// The handle the Player page uses on the library's background trickplay work: <see cref="Pause"/> while a recording opens (so ffmpeg
/// decoding does not compete with the first frame), <see cref="Resume"/> when it has played. Calls nest (several opens in a row).
/// The library's generator checks <see cref="IsPaused"/> / <see cref="Resumed"/> between jobs.
/// </summary>
public sealed class TrickplayQueue
{
    private int _pauses;

    public bool IsPaused => Volatile.Read(ref _pauses) > 0;

    /// <summary>Raised when the last pause was released.</summary>
    public event EventHandler? Resumed;

    public void Pause() => Interlocked.Increment(ref _pauses);

    public void Resume()
    {
        int now, seen;
        do
        {
            seen = Volatile.Read(ref _pauses);
            if (seen == 0) return;
            now = seen - 1;
        }
        while (Interlocked.CompareExchange(ref _pauses, now, seen) != seen);
        if (now == 0) Resumed?.Invoke(this, EventArgs.Empty);
    }
}
