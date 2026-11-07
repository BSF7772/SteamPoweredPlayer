// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;

namespace SteamPoweredPlayer.Services;

/// <summary>Scanner progress as the shell shows it (BusyLine + status). <see cref="Fraction"/> is null when indeterminate.</summary>
public sealed record ScanProgressInfo(bool Active, string Message, double? Fraction = null);

/// <summary>A timeline marker placed on a recording, for the Cache strip (offset in media seconds from the session start).</summary>
public sealed record MarkerSpot(double OffsetSeconds, string Glyph, string Label);

/// <summary>
/// What the UI needs from the library, nothing more. The event-driven <c>LibraryService</c> will implement it later;
/// the pages never see the scanner. Events may be raised from any thread: the UI marshals to its dispatcher.
/// </summary>
public interface ILibrarySource
{
    /// <summary>The latest index, or null while nothing is loaded yet.</summary>
    LibraryIndex? Current { get; }

    /// <summary>Raised after <see cref="Current"/> changed.</summary>
    event EventHandler? IndexChanged;

    /// <summary>Raised while a scan or index load runs, and once more with <c>Active = false</c> when it ends.</summary>
    event EventHandler<ScanProgressInfo>? ScanProgress;

    /// <summary>Loads the first index and starts watching for changes (idempotent).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Absolute path of art that the index names relative to the data folder (e.g. <c>art/730/hero.jpg</c>); null when not present.</summary>
    string? ResolveArt(string? relativePath);

    /// <summary>Markers for one recording (empty when unknown or not loaded yet).</summary>
    IReadOnlyList<MarkerSpot> MarkersFor(RecordingEntry recording);

    /// <summary>Why <see cref="Current"/> is null or stale, for the empty state; null when everything is fine.</summary>
    string? Problem { get; }
}
