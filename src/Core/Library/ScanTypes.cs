// SPDX-License-Identifier: GPL-3.0-or-later
namespace SteamPoweredPlayer.Core.Library;

/// <summary>One file of a session folder listing: name only (no path), size and last write time (UTC).</summary>
public readonly record struct ScanEntry(string Name, long Size, DateTimeOffset LastWriteUtc);

/// <summary>Result of the 8-byte head check of a chunk file.</summary>
public enum HeadCheck
{
    /// <summary>Starts with an MP4 box header (4-byte size, 4-character type).</summary>
    Valid,
    /// <summary>Starts with zero bytes (Steam leaves such chunks behind with normal names).</summary>
    ZeroFilled,
    /// <summary>Shorter than a box header.</summary>
    TooShort,
    /// <summary>Not zero, but not a plausible box header either.</summary>
    NotABox,
    /// <summary>The file or its folder was gone when the head was read.</summary>
    Vanished,
    /// <summary>Any other I/O failure.</summary>
    Unreadable,
}

/// <summary>What the start of <c>session.mpd</c> says. Never trusted for playback; only used for Live and Crashed.</summary>
public enum ManifestKind
{
    /// <summary>No <c>session.mpd</c> in the folder.</summary>
    Missing,
    /// <summary>Present but could not be read.</summary>
    Unreadable,
    /// <summary>Present and zero bytes long (may be mid-write).</summary>
    Empty,
    /// <summary>Only zero bytes: a crashed recording.</summary>
    ZeroFilled,
    /// <summary><c>type="dynamic"</c>: Steam is (or was) still writing.</summary>
    Dynamic,
    /// <summary>An MPD that is not dynamic.</summary>
    Static,
    /// <summary>Readable, not zero, no <c>&lt;MPD</c> element found.</summary>
    Unrecognised,
}

public enum ScanStatus
{
    /// <summary>Has init segments and at least one complete chunk; <see cref="ScanResult.Media"/> is set.</summary>
    Playable,
    /// <summary>The folder exists but nothing can be played (see <see cref="ScanResult.Issues"/>); Media is null.</summary>
    NotPlayable,
    /// <summary>The folder vanished (Steam trimmed it, or it was deleted mid-scan). Not an error.</summary>
    Gone,
}

public enum ScanIssueKind
{
    MissingInit,
    NoCompleteChunks,
    MissingAudioChunk,
    MissingVideoChunk,
    ZeroFilledChunk,
    InvalidChunkHead,
    VanishedChunk,
    UnreadableChunk,
    StaleTemporaryChunk,
    ZeroFilledManifest,
}

/// <summary>A reported problem. <see cref="File"/> is a file name inside the folder; <see cref="Chunk"/> the chunk number if known.</summary>
public sealed record ScanIssue(ScanIssueKind Kind, string? File = null, int? Chunk = null);

/// <summary>
/// Outcome of scanning one recording folder.
/// </summary>
/// <param name="Status">See <see cref="ScanStatus"/>.</param>
/// <param name="Media">The playable chunks; null unless <paramref name="Status"/> is <see cref="ScanStatus.Playable"/>.</param>
/// <param name="Crashed">Zero-filled manifest and/or stale <c>.tmp</c> chunks, and not live. Still playable from complete chunks.</param>
/// <param name="Issues">Everything that was rejected or looks wrong (diagnostics, never thrown).</param>
/// <param name="UnknownFiles">Names the scanner does not recognise, reported instead of guessed.</param>
/// <param name="Manifest">What <c>session.mpd</c> looked like.</param>
/// <param name="NewestChunkUtc">Last write time of the newest chunk file (complete or <c>.tmp</c>), if any.</param>
public sealed record ScanResult(
    ScanStatus Status,
    SessionMedia? Media,
    bool Crashed,
    IReadOnlyList<ScanIssue> Issues,
    IReadOnlyList<string> UnknownFiles,
    ManifestKind Manifest,
    DateTimeOffset? NewestChunkUtc)
{
    public static ScanResult Gone { get; } = new(ScanStatus.Gone, null, false, [], [], ManifestKind.Missing, null);
}

/// <summary>Options of <see cref="Scanner"/>. All optional.</summary>
public sealed record ScanOptions
{
    /// <summary>Clock for the Live and stale-<c>.tmp</c> decisions. Default <see cref="TimeProvider.System"/>.</summary>
    public TimeProvider? Clock { get; init; }

    /// <summary>Overrides the Temporary flag. Default: the folder name says cache session (<c>bg_*</c>).</summary>
    public bool? Temporary { get; init; }

    /// <summary>Test seam: runs after the directory listing, before the head checks (simulates Steam trimming mid-scan).</summary>
    internal Action? AfterListing { get; init; }
}
