// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.app.Application
import android.util.Log
import android.view.Surface
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.steampoweredplayer.AppSettings
import app.steampoweredplayer.Library
import app.steampoweredplayer.data.ChunkHeads
import app.steampoweredplayer.data.FolderNames
import app.steampoweredplayer.data.HeadCheck
import app.steampoweredplayer.data.LibraryLayout
import app.steampoweredplayer.data.LocalChunkCache
import app.steampoweredplayer.data.Marker
import app.steampoweredplayer.data.Markers
import app.steampoweredplayer.data.RecordingDetails
import app.steampoweredplayer.data.RecordingEntry
import app.steampoweredplayer.data.RecordingRepository
import app.steampoweredplayer.data.RemoteFileGone
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.data.SessionMedia
import app.steampoweredplayer.data.TrickplayIndex
import app.steampoweredplayer.data.TrickplayTile
import app.steampoweredplayer.data.key
import app.steampoweredplayer.data.readNBytesCompat
import app.steampoweredplayer.edit.AudioSync
import app.steampoweredplayer.edit.EditStore
import app.steampoweredplayer.edit.KeptRange
import app.steampoweredplayer.edit.RecordingEdit
import app.steampoweredplayer.edit.SyncAdvice
import app.steampoweredplayer.edit.SyncDetector
import app.steampoweredplayer.edit.SyncVerdict
import app.steampoweredplayer.export.ChapterRequest
import app.steampoweredplayer.export.DeviceEncoders
import app.steampoweredplayer.export.EncoderCap
import app.steampoweredplayer.export.ExportLadder
import app.steampoweredplayer.export.ExportPlan
import app.steampoweredplayer.export.ExportRequest
import app.steampoweredplayer.export.ExportScope
import app.steampoweredplayer.export.ExportScopes
import app.steampoweredplayer.export.ExportSpan
import app.steampoweredplayer.export.ExportSpans
import app.steampoweredplayer.export.Exports
import app.steampoweredplayer.export.FrameTables
import app.steampoweredplayer.export.LadderEngine
import app.steampoweredplayer.export.SourceFormat
import app.steampoweredplayer.export.SpanRequest
import app.steampoweredplayer.net.DownloadRequest
import app.steampoweredplayer.net.DownloadRoutes
import app.steampoweredplayer.net.Downloads
import app.steampoweredplayer.play.FetchPolicy
import app.steampoweredplayer.play.LoopHandles
import app.steampoweredplayer.play.MarkerKind
import app.steampoweredplayer.play.MarkerKinds
import app.steampoweredplayer.play.MarkerNavigation
import app.steampoweredplayer.play.MpvEngine
import app.steampoweredplayer.play.PlayableRuns
import app.steampoweredplayer.play.PlaybackSession
import app.steampoweredplayer.play.ReloadCause
import app.steampoweredplayer.play.TimelineGrab
import java.io.File
import java.io.IOException
import kotlin.math.abs
import kotlin.math.max
import kotlin.math.min
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.first
import kotlinx.coroutines.flow.map
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

data class MarkerUi(val marker: Marker, val kind: MarkerKind)

/** What's being asked of the detector, and what it said. */
data class SyncUi(val running: Boolean = false, val progress: Double = 0.0, val message: String? = null)

data class PlayerUi(
    val title: String = "",
    val loading: Boolean = true,
    val error: String? = null,
    val start: Double = 0.0,
    val end: Double = 0.0,
    val position: Double = 0.0,
    val paused: Boolean = false,
    val atEnd: Boolean = false,
    /** The playhead is waiting for a chunk that's still coming. */
    val waiting: Boolean = false,
    val waitingOffline: Boolean = false,
    val downloadPercent: Int = 0,
    val downloadBytesPerSecond: Long = 0,
    val downloadedRuns: List<Pair<Double, Double>> = emptyList(),
    val allHere: Boolean = false,
    val markers: List<MarkerUi> = emptyList(),
    val hiddenKinds: Set<MarkerKind> = emptySet(),
    val loopA: Double? = null,
    val loopB: Double? = null,
    val edit: RecordingEdit = RecordingEdit(),
    val sync: SyncUi = SyncUi(),
    val encoders: List<EncoderCap> = emptyList(),
    val sourceWidth: Int = 2560,
    val sourceHeight: Int = 1440,
    val sourceFps: Double = 60.0,
    val bytesPerSecond: Double = 0.0,
    val exportChoice: String = ExportLadder.DEFAULT_ID,
    val exportEngine: LadderEngine = LadderEngine.Auto,
    /** What the user picked in the Export sheet; null follows [ExportScopes.resolve]. */
    val exportScope: ExportScope? = null,
    val decoder: String? = null,
    val decodeIsCpu: Boolean = false,
)

/**
 * One open recording: the chunks on the phone played through a [PlaybackSession], the fetches that keep them coming, and the edit. The player only reads local files, so a lost
 * network never reaches the decoder; at the download edge it pauses on an overlay and resumes on its own.
 */
class PlayerViewModel(app: Application) : AndroidViewModel(app) {
    private val settings = AppSettings(app)
    private val edits = EditStore(File(app.filesDir, "edits"))
    private val _ui = MutableStateFlow(PlayerUi())
    val ui: StateFlow<PlayerUi> = _ui

    private var engine: MpvEngine? = null
    private var session: PlaybackSession? = null
    private var profile: ServerProfile? = null
    private var entry: RecordingEntry? = null
    private var details: RecordingDetails? = null
    private var full: SessionMedia? = null

    @Volatile private var frameTables: FrameTables? = null
    private var cache: LocalChunkCache? = null
    private var libraryKey = ""
    private val available = HashSet<Int>()
    private val headChecked = HashMap<Int, Boolean>()
    private var waitingAt: Double? = null
    private var wantPlay = true
    private var surfaceReady = false
    private var opened = false
    private var lastChunk = -1
    private var lastUiPosition = Double.NaN
    private var trick: TrickplayIndex? = null
    private var trickDir: File? = null
    private var openedAt = 0L
    private var reloadRetried = false
    private var speedBytes = 0L
    private var speedTime = 0L
    private var syncJob: Job? = null

    val trickplayDir: File? get() = trickDir

    fun tileAt(seconds: Double): TrickplayTile? = trick?.findNearest(seconds)

    /** Opens a recording once; a rotation keeps what's there. */
    fun open(profile: ServerProfile, entry: RecordingEntry, layout: LibraryLayout?, fromScan: Boolean, title: String, startAt: Double?) {
        if (this.entry != null) return
        openedAt = android.os.SystemClock.elapsedRealtime()
        this.profile = profile
        this.entry = entry
        libraryKey = DownloadRoutes.libraryKey(profile)
        val edit = edits.load(entry.key)
        _ui.update {
            it.copy(
                title = title, edit = edit, paused = false, exportChoice = settings.exportChoice,
                exportEngine = LadderEngine.entries.firstOrNull { e -> e.name == settings.exportEngine } ?: LadderEngine.Auto,
                decodeIsCpu = settings.decodeMode == app.steampoweredplayer.play.DecodeMode.Cpu,
            )
        }
        viewModelScope.launch {
            try {
                prepare(profile, entry, layout, fromScan, startAt ?: edit.resumeSeconds)
            } catch (e: IOException) {
                _ui.update { it.copy(loading = false, error = e.message ?: "Couldn't open this recording.") }
            }
        }
        viewModelScope.launch { Downloads.progress.collect(::onDownloadProgress) }
    }

    private suspend fun prepare(profile: ServerProfile, entry: RecordingEntry, layout: LibraryLayout?, fromScan: Boolean, start: Double?) {
        val app = getApplication<Application>()
        val repo = withContext(Dispatchers.IO) { Library(app, profile).recordings(layout) }  // opens the vault and makes the SMB context
        val chunkCache = withContext(Dispatchers.IO) { Downloads.cache(app) }
        cache = chunkCache
        // What's stored opens at once and offline; the share then says what changed.
        mark("repo, cache")
        val stored = withContext(Dispatchers.IO) { repo.stored(entry.key) }
        mark("details")
        val loaded = stored ?: withContext(Dispatchers.IO) {
            try {
                repo.load(entry, checkHeads = fromScan)
            } catch (e: RemoteFileGone) {
                throw IOException("Steam no longer has this recording.")
            } catch (e: IOException) {
                throw IOException("Can't reach the server, and this recording hasn't been opened on this phone yet.")
            }
        }
        details = loaded
        val folder = chunkCache.folderOf(libraryKey, entry.key)
        val media = loaded.media(folder.path)
        if (media.chunkCount == 0) throw IOException("Steam no longer has this recording.")
        full = media
        frameTables = FrameTables(folder)
        val visible = withContext(Dispatchers.IO) { markersAndTrickplay(repo, loaded, media) }
        mark("markers")
        val bytesPerSecond = if (entry.durationSeconds > 0) entry.sizeBytes / entry.durationSeconds else 0.0
        val encoders = withContext(Dispatchers.Default) { DeviceEncoders.list() }
        mark("encoders")
        _ui.update {
            it.copy(
                start = media.startSeconds, end = media.endSeconds, position = media.startSeconds, markers = visible, encoders = encoders, bytesPerSecond = bytesPerSecond,
            )
        }
        withContext(Dispatchers.IO) { refreshAvailable(chunkCache, folder, media) }
        mark("heads")
        publishDownloaded()
        viewModelScope.launch {
            chunkCache.completed.collect { key ->
                if (key.libraryId == libraryKey && key.recording == entry.key) onFileArrived()
            }
        }

        val target = start?.takeIf { it > media.startSeconds && it < media.endSeconds - 1 } ?: media.startSeconds
        waitingAt = target
        fetch(media.chunkAt(target).toInt())
        startWhenPossible()
        if (stored != null) {
            viewModelScope.launch(Dispatchers.IO) { refreshFromShare(repo, entry, fromScan, folder) }
        }
    }

    /** The share's newer facts about a recording that's already open: Steam may have trimmed it, and the desktop may have made its markers or thumbnails since. */
    private suspend fun refreshFromShare(repo: RecordingRepository, entry: RecordingEntry, fromScan: Boolean, folder: File) {
        val fresh = try {
            repo.load(entry, checkHeads = fromScan)
        } catch (_: IOException) {
            return                                                                    // offline, or gone from the share: what's on the phone still plays
        }
        val media = fresh.media(folder.path)
        if (media.chunkCount == 0) return
        details = fresh
        full = media
        val visible = markersAndTrickplay(repo, fresh, media)
        withContext(Dispatchers.Main) {
            _ui.update { it.copy(start = media.startSeconds, end = media.endSeconds, markers = visible) }
            publishDownloaded()
        }
    }

    private fun markersAndTrickplay(repo: RecordingRepository, d: RecordingDetails, media: SessionMedia): List<MarkerUi> {
        repo.trickplay(d.key)?.let {
            trick = TrickplayIndex(it, media.axisShiftSeconds)
            trickDir = repo.trickDir(d.key)
        }
        return Markers.place(repo.markers(d).markers, media).visible
            .map { MarkerUi(it, MarkerKinds.classify(it.entry)) }.filter { MarkerKinds.showOnLane(it.kind) }
    }

    private fun refreshAvailable(c: LocalChunkCache, folder: File, media: SessionMedia) {
        val here = c.chunksOf(libraryKey, entry!!.key)
        synchronized(available) {
            val init = (0..1).all { File(folder, FolderNames.initFile(it)).exists() }
            available.clear()
            if (!init) return
            for (n in here) {
                if (!media.contains(n)) continue
                val good = headChecked.getOrPut(n) {
                    (0..1).all { s -> headOf(File(folder, FolderNames.chunkFile(s, n))) == HeadCheck.Valid }
                }
                if (good) available.add(n)
            }
        }
    }

    private fun headOf(file: File): HeadCheck = try {
        file.inputStream().use { ChunkHeads.classify(it.readNBytesCompat(ChunkHeads.HEAD_LENGTH)) }
    } catch (_: IOException) {
        HeadCheck.Unreadable
    }

    private fun availableSnapshot(): Set<Int> = synchronized(available) { available.toSet() }

    private suspend fun onFileArrived() {
        val media = full ?: return
        val c = cache ?: return
        withContext(Dispatchers.IO) { refreshAvailable(c, c.folderOf(libraryKey, entry!!.key), media) }
        publishDownloaded()
        val s = session
        val here = availableSnapshot()
        val at = waitingAt
        if (at != null) {
            startWhenPossible()
        } else if (s != null) {
            PlayableRuns.around(media, here, s.currentPosition)?.let(s::replaceMedia)     // the stretch grew: nothing to reload
        }
    }

    private fun publishDownloaded() {
        val media = full ?: return
        val here = availableSnapshot()
        val runs = SessionMedia.runsOf(here.sorted()).map { max(media.chunkStartSeconds(it.first), media.startSeconds) to min(media.chunkEndSeconds(it.last), media.endSeconds) }
        val all = media.chunks().all { it in here }
        _ui.update { it.copy(downloadedRuns = runs, allHere = all, downloadPercent = if (media.chunkCount > 0) here.count(media::contains) * 100 / media.chunkCount else 0) }
    }

    /** Asks for what the player needs next: the whole recording when small, a stretch ahead of the playhead when long. */
    private fun fetch(playheadChunk: Int) {
        val media = full ?: return
        val p = profile ?: return
        val e = entry ?: return
        val d = details ?: return
        val app = getApplication<Application>()
        val selection = FetchPolicy.selection(media, e.sizeBytes, playheadChunk)
        val here = availableSnapshot()
        val missing = selection.filterNot { it in here }
        val initMissing = cache?.let { c -> (0..1).any { File(c.folderOf(libraryKey, e.key), FolderNames.initFile(it)).exists().not() } } ?: true
        viewModelScope.launch {
            Downloads.seek(e.key, playheadChunk)
            if (missing.isNotEmpty() || initMissing) Downloads.fetch(app, DownloadRequest(p.id, e.key, d.mediaFolder, selection, playheadChunk))
        }
    }

    private fun onDownloadProgress(all: Map<String, app.steampoweredplayer.data.DownloadProgress>) {
        val p = all[entry?.key ?: return]
        val now = System.currentTimeMillis()
        if (p == null) {
            speedBytes = 0
            speedTime = 0
            _ui.update { it.copy(downloadBytesPerSecond = 0) }
            return
        }
        if (speedTime != 0L && now - speedTime >= SPEED_EVERY_MILLIS) {
            val rate = (p.bytesFromSource - speedBytes) * 1000 / (now - speedTime)
            _ui.update { it.copy(downloadBytesPerSecond = max(0, rate)) }
            speedBytes = p.bytesFromSource
            speedTime = now
        } else if (speedTime == 0L) {
            speedBytes = p.bytesFromSource
            speedTime = now
        }
        _ui.update { it.copy(waitingOffline = p.status == app.steampoweredplayer.data.DownloadStatus.CannotReach) }
    }

    // ---- the surface and the engine ----------------------------------------------------------------------------

    fun surfaceCreated(surface: Surface) {
        surfaceReady = true
        val e = engine ?: MpvEngine(getApplication(), settings.decodeMode).also { engine = it }
        e.attachSurface(surface)
        if (session == null) {
            session = PlaybackSession(e, viewModelScope, frameTimes = { chunk -> frameTables?.of(chunk) }).also { s ->
                s.onPosition = ::onPosition
                s.onReachedEnd = ::onReachedEnd
                s.onReloaded = { _, t -> onReloaded(t) }
                s.onReloadFailed = { failure -> onReloadFailed(failure) }
            }
            val edit = _ui.value.edit
            e.setSpeed(edit.speed)
            e.setVolume(edit.volumePercent)
            e.setMute(edit.muted)
            e.setAudioDelay(edit.audioDelay)
        }
        startWhenPossible()
    }

    fun surfaceChanged(width: Int, height: Int) {
        engine?.surfaceSize(width, height)
    }

    fun surfaceDestroyed() {
        surfaceReady = false
        engine?.detachSurface()
    }

    /** Loads the waiting position once the chunk is here and there's somewhere to show it. */
    private fun startWhenPossible() {
        val s = session ?: return
        val media = full ?: return
        val at = waitingAt ?: return
        if (!surfaceReady) return
        val here = availableSnapshot()
        val next = PlayableRuns.nextChunkAt(media, at) ?: return
        val target = max(at, media.chunkStartSeconds(next))
        val run = PlayableRuns.around(media, here, target)
        if (run == null) {
            _ui.update { it.copy(waiting = true, loading = !opened && it.loading) }
            return
        }
        waitingAt = null
        val first = !opened
        opened = true
        _ui.update { it.copy(waiting = false, loading = false, paused = !wantPlay, atEnd = false) }
        if (first) {
            mark("load starts")
            s.open(run, target, wantPlay)
        } else {
            s.replaceMedia(run)
            s.setPause(!wantPlay)
            s.reload(target, ReloadCause.Seek, force = true)
        }
    }

    private fun mark(stage: String) {
        if (openedAt != 0L) Log.i(TAG, "opening: $stage at ${android.os.SystemClock.elapsedRealtime() - openedAt} ms")
    }

    private fun onReloaded(target: Double) {
        if (openedAt != 0L) {
            Log.i(TAG, "first frame ${android.os.SystemClock.elapsedRealtime() - openedAt} ms after opening, ${availableSnapshot().size} chunks here")
            openedAt = 0
        }
        reloadRetried = false
        // A load that finishes while the playhead waits for another chunk says nothing about where the playhead is.
        _ui.update { it.copy(position = if (waitingAt == null) target else it.position, error = null, decoder = engine?.hwdecCurrent()) }
    }

    private fun onReloadFailed(failure: Exception) {
        Log.w(TAG, "reload failed: ${failure.javaClass.simpleName}")
        val s = session
        if (s != null && !reloadRetried) {                                            // a slow load isn't a broken recording: once more before saying so
            reloadRetried = true
            s.reload(s.currentPosition, ReloadCause.Seek)
            return
        }
        _ui.update { it.copy(error = "Couldn't play this part: ${failure.message}") }
    }

    private fun onPosition(t: Double) {
        if (waitingAt != null) return
        if (abs(t - lastUiPosition) >= POSITION_STEP || lastUiPosition.isNaN()) {
            lastUiPosition = t
            _ui.update { it.copy(position = t, atEnd = false) }
        }
        val media = full ?: return
        val chunk = media.chunkAt(t).toInt()
        if (chunk != lastChunk) {
            lastChunk = chunk
            val here = availableSnapshot()
            if (FetchPolicy.needsTopUp(media, here, chunk)) fetch(chunk)
        }
    }

    private fun onReachedEnd(t: Double) {
        val media = full ?: return
        val s = session ?: return
        if (t >= media.endSeconds - END_TOLERANCE) {
            _ui.update { it.copy(atEnd = true, paused = true, position = media.endSeconds) }
            s.setPause(true)
            return
        }
        // The end of what's loaded, not of the recording. The stretch on the phone may have grown past it meanwhile.
        val run = s.currentWindow.lastOrNull() ?: return
        val next = PlayableRuns.chunkAfter(media, run)
        if (next == null) {
            _ui.update { it.copy(atEnd = true, paused = true) }
            return
        }
        wantPlay = !s.isPaused
        waitingAt = media.chunkStartSeconds(next)
        fetch(next)
        viewModelScope.launch { startWhenPossible() }
    }

    // ---- what the controls do ------------------------------------------------------------------------------------

    fun togglePause() {
        val s = session ?: return
        val ui = _ui.value
        if (waitingAt != null) {
            wantPlay = !wantPlay
            _ui.update { it.copy(paused = !wantPlay) }
            return
        }
        if (ui.atEnd) {
            wantPlay = true
            seek(ui.start)
            return
        }
        val paused = !s.isPaused
        s.setPause(paused)
        wantPlay = !paused
        _ui.update { it.copy(paused = paused) }
    }

    /** A seek inside what's on the phone is a reload; elsewhere it waits for the chunk, which comes next. */
    fun seek(seconds: Double) {
        val media = full ?: return
        val s = session ?: return
        val target = PlaybackSession.snap(media, seconds)
        val here = availableSnapshot()
        val run = PlayableRuns.around(media, here, target)
        lastUiPosition = target
        _ui.update { it.copy(position = target, atEnd = false) }
        if (run == null) {
            wantPlay = if (waitingAt != null) wantPlay else !s.isPaused
            waitingAt = target
            if (opened) s.setPause(true)
            fetch(media.chunkAt(target).toInt())
            startWhenPossible()
            return
        }
        waitingAt = null
        _ui.update { it.copy(waiting = false) }
        val current = s.currentMedia
        if (!opened) {
            opened = true
            s.open(run, target, wantPlay)
        } else if (current == null || current.runs != run.runs) {
            s.replaceMedia(run)
            s.reload(target, ReloadCause.Seek, force = true)
        } else {
            s.seek(target)
        }
        if (FetchPolicy.needsTopUp(media, here, media.chunkAt(target).toInt())) fetch(media.chunkAt(target).toInt())
    }

    fun seekBy(delta: Double) = seek(_ui.value.position + delta)

    fun frameForward() {
        session?.frameStep()
        wantPlay = false
        _ui.update { it.copy(paused = true) }
    }

    fun frameBack() {
        session?.stepBack()
        wantPlay = false
        _ui.update { it.copy(paused = true) }
    }

    fun nextEvent() {
        val shown = shownMarkers()
        MarkerNavigation.next(shown, _ui.value.position)?.let { seek(MarkerNavigation.landing(it)) }
    }

    fun previousEvent() {
        val shown = shownMarkers()
        MarkerNavigation.previous(shown, _ui.value.position)?.let { seek(MarkerNavigation.landing(it)) }
    }

    fun toggleKind(kind: MarkerKind) = _ui.update { it.copy(hiddenKinds = if (kind in it.hiddenKinds) it.hiddenKinds - kind else it.hiddenKinds + kind) }

    private fun shownMarkers() = _ui.value.let { ui -> ui.markers.filter { it.kind !in ui.hiddenKinds }.map { it.marker } }

    fun setSpeed(speed: Double) {
        engine?.setSpeed(speed)
        updateEdit { it.copy(speed = speed) }
    }

    fun setVolume(percent: Double) {
        engine?.setVolume(percent)
        updateEdit { it.copy(volumePercent = percent) }
    }

    fun toggleMute() {
        val muted = !_ui.value.edit.muted
        engine?.setMute(muted)
        updateEdit { it.copy(muted = muted) }
    }

    /** The sound against the picture, live. Positive = later. */
    fun setAudioDelay(seconds: Double) {
        engine?.setAudioDelay(seconds)
        updateEdit { it.copy(audioDelay = seconds) }
    }

    /** Puts the A-B handles on the timeline, around the playhead, for dragging. */
    fun startLoop() {
        val ui = _ui.value
        if (ui.loopA != null) return
        val (a, b) = LoopHandles.initial(ui.position, ui.start, ui.end)
        _ui.update { it.copy(loopA = a, loopB = b) }
        session?.setLoop(a, b)
    }

    fun clearLoop() {
        _ui.update { it.copy(loopA = null, loopB = null) }
        session?.setLoop(null, null)
    }

    /** A handle follows the finger; the loop itself changes when the finger lifts. */
    fun dragLoopA(seconds: Double) = _ui.update { it.copy(loopA = LoopHandles.moveA(seconds, it.loopB ?: it.end, it.start)) }

    fun dragLoopB(seconds: Double) = _ui.update { it.copy(loopB = LoopHandles.moveB(seconds, it.loopA ?: it.start, it.end)) }

    /** The finger lifted: the loop takes the new range and the picture shows the handle that moved. */
    fun finishLoopDrag(grab: TimelineGrab) {
        val ui = _ui.value
        val a = ui.loopA ?: return
        val b = ui.loopB ?: return
        session?.setLoop(a, b)
        when (grab) {
            TimelineGrab.A -> seek(a)
            TimelineGrab.B -> seek(max(a, b - LOOP_PREVIEW_SECONDS))
            TimelineGrab.Seek -> Unit
        }
    }

    /** The A-B range becomes one of the cuts an export takes. */
    fun addLoopToCuts() {
        val ui = _ui.value
        val a = ui.loopA ?: return
        val b = ui.loopB ?: return
        updateEdit { edit ->
            edit.copy(kept = ExportSpans.normalize((edit.kept + KeptRange(a, b)).map { ExportSpan(it.start, it.end) }).map { KeptRange(it.start, it.end) })
        }
    }

    fun removeRange(range: KeptRange) = updateEdit { it.copy(kept = it.kept - range) }

    fun clearEdit() = updateEdit { it.copy(kept = emptyList(), markIn = null, markOut = null) }

    fun chooseExportScope(scope: ExportScope) = _ui.update { it.copy(exportScope = scope) }

    /** A highlight reel: ± a few seconds around each shown event. */
    fun keepHighlights() {
        val media = full ?: return
        val spans = ExportSpans.highlights(media, shownMarkers().map { it.startSeconds })
        updateEdit { it.copy(kept = spans.map { s -> KeptRange(s.start, s.end) }, markIn = null, markOut = null) }
    }

    private fun updateEdit(change: (RecordingEdit) -> RecordingEdit) {
        val key = entry?.key ?: return
        _ui.update { it.copy(edit = change(it.edit)) }
        val edit = _ui.value.edit
        viewModelScope.launch(Dispatchers.IO) { edits.save(key, edit) }
    }

    /** The part of the recording an export takes right now. */
    fun exportScope(): ExportScope = _ui.value.let { ExportScopes.resolve(it.exportScope, it.loopA != null && it.loopB != null, it.edit.kept.isNotEmpty()) }

    /** What an export or a detection works on: the choice in the Export sheet, by default the cuts, else the A-B range, else the whole recording. */
    fun exportSpans(): List<ExportSpan> {
        val ui = _ui.value
        return when (exportScope()) {
            ExportScope.Cuts -> ui.edit.kept.map { ExportSpan(it.start, it.end) }
            ExportScope.Loop -> listOf(ExportSpan(ui.loopA ?: ui.start, ui.loopB ?: ui.end))
            ExportScope.Whole -> listOf(ExportSpan(ui.start, ui.end))
        }
    }

    /** How long a part of the recording is, in the source's own seconds. */
    fun scopeSeconds(scope: ExportScope): Double {
        val ui = _ui.value
        return when (scope) {
            ExportScope.Cuts -> ui.edit.kept.sumOf { it.end - it.start }
            ExportScope.Loop -> (ui.loopB ?: ui.end) - (ui.loopA ?: ui.start)
            ExportScope.Whole -> ui.end - ui.start
        }
    }

    fun detectSync() {
        val media = full ?: return
        val c = cache ?: return
        val p = profile ?: return
        val e = entry ?: return
        val d = details ?: return
        if (_ui.value.sync.running) return
        val spans = exportSpans()
        val mid = spans.first()
        syncJob = viewModelScope.launch {
            _ui.update { it.copy(sync = SyncUi(running = true)) }
            try {
                val folder = c.folderOf(libraryKey, e.key)
                val local = media.copy(folder = folder.path)
                val needed = SyncDetector.chunks(local, mid)
                val missing = needed.filterNot { it in availableSnapshot() }
                if (missing.isNotEmpty()) {
                    Downloads.fetch(getApplication(), DownloadRequest(p.id, e.key, d.mediaFolder, needed, needed.first()))
                    _ui.update { it.copy(sync = SyncUi(running = true, message = "Fetching the part to listen to…")) }
                    c.completed.collectUntil { needed.all { n -> n in c.chunksOf(libraryKey, e.key) } }
                }
                val result = withContext(Dispatchers.Default) {
                    SyncDetector.detect(local, mid) { f -> _ui.update { it.copy(sync = it.sync.copy(progress = f, message = "Listening…")) } }
                }
                val advice = SyncAdvice.from(result)
                val message = when (advice.verdict) {
                    SyncVerdict.NotApplied -> "Low confidence: not applied"
                    SyncVerdict.InSync -> "In sync: nothing to fix (${"%+.0f".format(result.audioLate * 1000)} ms)"
                    SyncVerdict.Fix -> "Sound ${"%+.0f".format(result.audioLate * 1000)} ms: fixed"
                }
                if (advice.verdict == SyncVerdict.Fix) setAudioDelay(advice.audioDelay)
                Log.i(TAG, "sync: ${result.confidence} ${result.agreeing}/${result.consistent}")
                _ui.update { it.copy(sync = SyncUi(message = message)) }
            } catch (failure: IOException) {
                _ui.update { it.copy(sync = SyncUi(message = "Couldn't analyse this part: ${failure.message}")) }
            }
        }
    }

    fun setExportChoice(id: String) {
        settings.exportChoice = id
        _ui.update { it.copy(exportChoice = id) }
    }

    fun setExportEngine(engine: LadderEngine) {
        settings.exportEngine = engine.name
        _ui.update { it.copy(exportEngine = engine) }
    }

    /** Reads the source's real size for the sheet, once its first chunk is here. */
    fun loadSourceFormat() {
        val media = full ?: return
        viewModelScope.launch(Dispatchers.IO) {
            try {
                val f = SourceFormat.read(media)
                _ui.update { it.copy(sourceWidth = f.width, sourceHeight = f.height, sourceFps = f.fps) }
            } catch (_: IOException) {
                Unit
            }
        }
    }

    /** Queues an export of the kept ranges with the chosen tier; [share] opens the share sheet when it's done, with that same tier. */
    fun export(stem: String, targetMegabytes: Double?, share: Boolean) {
        val ui = _ui.value
        val p = profile ?: return
        val e = entry ?: return
        val choice = ui.exportChoice
        val spans = exportSpans().map { SpanRequest(it.start, it.end) }
        val saveClip = choice == ExportLadder.SAVE_CLIP_ID
        val chapters = if (saveClip) ui.markers.filter { it.kind !in ui.hiddenKinds }.map { ChapterRequest(it.marker.startSeconds, it.marker.entry.title ?: it.kind.name) } else emptyList()
        Exports.enqueue(
            getApplication(),
            ExportRequest(
                profileId = p.id, libraryKey = libraryKey, recording = e.key, title = ui.title, stem = stem, spans = spans, choice = choice, engine = ui.exportEngine,
                targetMegabytes = targetMegabytes, speed = if (saveClip) 1.0 else ui.edit.speed,
                volumePercent = if (saveClip) 100.0 else ui.edit.volumePercent, muted = if (saveClip) false else ui.edit.muted,
                audioDelay = ui.edit.audioDelay, chapters = chapters, share = share,
            ),
        )
    }

    /** What a Save clip weighs: it stores the whole chunks of each kept range, each chunk once. */
    fun saveClipBytes(): Long {
        val media = full ?: return 0
        val plans = ExportSpans.plans(media, exportSpans())
        val chunks = plans.flatMap { it.firstChunk..it.lastChunk }.filter(media::contains).distinct().size
        return (_ui.value.bytesPerSecond * chunks * media.segmentSeconds).toLong()
    }

    /** The output's length with the kept ranges and, for a re-encode, the speed. */
    fun outputSeconds(choice: String): Double {
        val media = full ?: return 0.0
        val plans = exportSpans().mapNotNull { ExportPlan.of(media, it.start, it.end) }
        return ExportSpans.outputSeconds(plans, if (choice == ExportLadder.SAVE_CLIP_ID) 1.0 else _ui.value.edit.speed)
    }

    /** Remembers where playback stopped. */
    fun pauseForBackground() {
        val s = session ?: return
        if (!s.isPaused) {
            s.setPause(true)
            wantPlay = false
            _ui.update { it.copy(paused = true) }
        }
        saveResume()
    }

    private fun saveResume() {
        val ui = _ui.value
        if (entry != null && !ui.loading) updateEdit { it.copy(resumeSeconds = ui.position.takeIf { p -> p < ui.end - 2 }) }
    }

    /** A long recording's window of chunks was only for watching: leaving stops it, unless it's kept offline or an export is waiting for those chunks. A small recording finishes. */
    private fun stopWatchingFetch() {
        val e = entry ?: return
        val media = full ?: return
        if (FetchPolicy.selection(media, e.sizeBytes, media.firstChunk ?: 1).size >= media.chunkCount) return
        if (cache?.isPinned(libraryKey, e.key) == true) return
        val exporting = Exports.active.value?.request?.recording == e.key || Exports.queued.value.any { it.recording == e.key }
        if (!exporting) Downloads.cancel(getApplication(), e.key)
    }

    override fun onCleared() {
        stopWatchingFetch()
        saveResume()
        syncJob?.cancel()
        session?.close()
        engine?.let { e ->
            if (surfaceReady) e.detachSurface()
            e.destroy()
        }
        engine = null
    }

    private suspend fun SharedFlow<*>.collectUntil(done: () -> Boolean) {
        if (done()) return
        map { done() }.first { it }
    }

    private companion object {
        const val TAG = "SppPlayer"
        const val POSITION_STEP = 0.05
        const val END_TOLERANCE = 0.15
        const val SPEED_EVERY_MILLIS = 1000L

        /** After moving B, playback starts this far before it, so the end of the loop is what's seen next. */
        const val LOOP_PREVIEW_SECONDS = 2.0
    }
}
