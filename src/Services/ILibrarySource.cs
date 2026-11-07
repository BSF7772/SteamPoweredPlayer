// SPDX-License-Identifier: GPL-3.0-or-later
using SteamPoweredPlayer.Core.Library;
using SteamPoweredPlayer.Core.Steam;

namespace SteamPoweredPlayer.Services;

/// <summary>Scanner progress as the shell shows it (BusyLine + status). <see cref="Fraction"/> is null when indeterminate.</summary>
public sealed record ScanProgressInfo(bool Active, string Message, double? Fraction = null);

/// <summary>A timeline marker placed on a recording, for the Cache strip (offset in media seconds from the session start).</summary>
public sealed record MarkerSpot(double OffsetSeconds, string Glyph, string Label);

/// <summary>Raised with <see cref="ILibrarySource.IndexChanged"/>: the folders that were added, changed or removed since the previous event (all of them for the first one).</summary>
public sealed class IndexChangedArgs(IReadOnlyCollection<string> changedFolders) : EventArgs
{
    public IReadOnlyCollection<string> ChangedFolders { get; } = changedFolders;
}

/// <summary>Raised with <see cref="ILibrarySource.MediaChanged"/>: the playable media of <see cref="RelativeFolder"/> changed (null = the recording is gone).</summary>
public sealed record MediaChangedArgs(string RelativeFolder, SessionMedia? Media);

/// <summary>
/// What the UI needs from the library, nothing more. The event-driven <c>LibraryService</c> will implement it later;
/// the pages never see the scanner. Events may be raised from any thread: the UI marshals to its dispatcher.
/// </summary>
public interface ILibrarySource
{
    /// <summary>The latest index, or null while nothing is loaded yet.</summary>
    LibraryIndex? Current { get; }

    /// <summary>Raised after <see cref="Current"/> changed.</summary>
    event EventHandler<IndexChangedArgs>? IndexChanged;

    /// <summary>Raised when the media of one recording changed (a live session grew, Steam trimmed it, it vanished). On the UI thread.</summary>
    event EventHandler<MediaChangedArgs>? MediaChanged;

    /// <summary>Raised on the UI thread when markers or trickplay of a recording became available or changed (<c>relativeFolder</c>).</summary>
    event EventHandler<string>? DerivedChanged;

    /// <summary>Raised while a scan or index load runs, and once more with <c>Active = false</c> when it ends.</summary>
    event EventHandler<ScanProgressInfo>? ScanProgress;

    /// <summary>Loads the first index and starts watching for changes (idempotent).</summary>
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Absolute path of art that the index names relative to the data folder (e.g. <c>art/730/hero.jpg</c>); null when not present.</summary>
    string? ResolveArt(string? relativePath);

    /// <summary>Markers for one recording (empty when unknown or not loaded yet).</summary>
    IReadOnlyList<MarkerSpot> MarkersFor(RecordingEntry recording);

    /// <summary>The playable media (chunk runs on disk) of a recording, or null when unknown, not scanned yet, or gone.</summary>
    SessionMedia? GetMedia(string relativeFolder);

    /// <summary>
    /// All markers of a recording on the media-time axis (exact offset from gamerecording.pb / clip.pb field 1.5.10, folder-name fallback).
    /// Returns what is cached now (empty when not loaded yet) and never blocks; loading runs in the background and raises <see cref="DerivedChanged"/>.
    /// </summary>
    IReadOnlyList<Marker> FullMarkersFor(string relativeFolder);

    /// <summary>Trickplay (filmstrip) of a recording, or null while none exists. Cached; reads the manifest on first use.</summary>
    TrickplayIndex? TrickplayFor(string relativeFolder);

    /// <summary>Why <see cref="Current"/> is null or stale, for the empty state; null when everything is fine.</summary>
    string? Problem { get; }
}
