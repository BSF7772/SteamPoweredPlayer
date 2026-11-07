// SPDX-License-Identifier: GPL-3.0-or-later
using System.Globalization;
using System.Text;
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Core.Playback;

/// <summary>
/// The lead mpv EDL (docs/steam-recording-format.md, "Playback with mpv"): a video track and an audio track of
/// <c>!mp4_dash</c> parts over the chunk files actually on disk. Pure: no I/O, no mpv.
/// </summary>
public static class Edl
{
    /// <summary>
    /// Builds the EDL for <paramref name="chunks"/>, the sorted, distinct, head-validated chunk numbers of one window.
    /// </summary>
    /// <remarks>
    /// Axis derivation. Chunk N starts at (N − 1)·S on the tfdt axis (S = <see cref="SessionMedia.SegmentSeconds"/>) and mpv lays EDL
    /// parts end to end from 0, so the parts must tile [0, ·) such that every chunk's start falls where its part starts:
    /// <list type="bullet">
    /// <item>Part k (k ≥ 1) starts at (n_k − 1)·S, the end of part k − 1, and ends at (next − 1)·S, where next is the following chunk
    /// number (n_k + 1 for the last part: it has nothing after it). Its length is (next − n_k)·S: 1·S normally, and (gap + 1)·S before
    /// a gap, because the part is stretched over the hole.</item>
    /// <item>Part 0 starts at 0 although its chunk starts at (n_0 − 1)·S: it also covers everything before the window (the "lead").
    /// Its length is (next − 1)·S, which is n_0·S when it is followed directly by chunk n_0 + 1, and n_0·S plus the gap stretch
    /// when a gap follows it too.</item>
    /// </list>
    /// The parts therefore end at (n_last)·S and mpv's <c>time-pos</c> equals the session's media time. Every part length is the integer
    /// number of chunks times S, so it is exact in a double and written with the invariant culture.
    /// <para>With an axis shift A (<see cref="SessionMedia.AxisShiftSeconds"/>: a saved clip, whose chunk 1 sits at its real tfdt) every
    /// start above moves by A; only part 0 changes length (it starts at 0 either way), by + A. A is a whole number of segments, so the
    /// lengths stay exact. mp4_dash plays the fragments' own timestamps, so without the shift a clip's time-pos is its tfdt (e.g. 588 s)
    /// on an axis whose parts end at 6 s.</para>
    /// </remarks>
    /// <param name="media">Supplies the folder and the segment length (its runs are not consulted: <paramref name="chunks"/> is the window).</param>
    /// <param name="url">Maps a local path (forward slashes) to what mpv opens: the path itself, a cache path or a loopback URL.</param>
    /// <exception cref="ArgumentException"><paramref name="chunks"/> is empty or not strictly ascending.</exception>
    public static string Build(SessionMedia media, IReadOnlyList<int> chunks, Func<string, string> url)
    {
        ArgumentNullException.ThrowIfNull(media);
        ArgumentNullException.ThrowIfNull(chunks);
        ArgumentNullException.ThrowIfNull(url);
        if (chunks.Count == 0) throw new ArgumentException("An EDL needs at least one chunk.", nameof(chunks));
        for (var i = 1; i < chunks.Count; i++)
            if (chunks[i] <= chunks[i - 1]) throw new ArgumentException("Chunk numbers must be strictly ascending.", nameof(chunks));

        var dir = media.Folder.Replace('\\', '/').TrimEnd('/');
        var sb = new StringBuilder("# mpv EDL v0\n!no_chapters\n", 128 + chunks.Count * 2 * (dir.Length + 60));
        Track(sb, media, dir, chunks, 0, url);
        sb.Append("!new_stream\n");
        Track(sb, media, dir, chunks, 1, url);
        return sb.ToString();
    }

    private static void Track(StringBuilder sb, SessionMedia media, string dir, IReadOnlyList<int> chunks, int stream, Func<string, string> url)
    {
        sb.Append("!mp4_dash,init=");
        AppendEscaped(sb, url(dir + "/" + FolderNames.InitFile(stream)));
        sb.Append('\n');

        var seg = media.SegmentSeconds;
        for (var i = 0; i < chunks.Count; i++)
        {
            var n = chunks[i];
            var next = i + 1 < chunks.Count ? chunks[i + 1] : n + 1;
            // First part: from 0 to the end of its stretch, plus the axis shift (a clip's chunk 1 starts at its real tfdt);
            // others: n → next. A shift of 0 adds nothing, so cache-session EDLs are unchanged byte for byte.
            var length = i == 0 ? (next - 1) * seg + media.AxisShiftSeconds : (next - n) * seg;
            AppendEscaped(sb, url(dir + "/" + FolderNames.ChunkFile(stream, n)));
            sb.Append(",length=").Append(length.ToString(CultureInfo.InvariantCulture)).Append('\n');
        }
    }

    /// <summary>mpv's length-prefixed value syntax: <c>%&lt;utf8 byte count&gt;%value</c> (commas, <c>%</c>, newlines inside are safe).</summary>
    public static string Escape(string value) => string.Create(CultureInfo.InvariantCulture, $"%{Encoding.UTF8.GetByteCount(value)}%{value}");

    private static void AppendEscaped(StringBuilder sb, string value) =>
        sb.Append('%').Append(Encoding.UTF8.GetByteCount(value)).Append('%').Append(value);
}
