// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.app.Application
import android.util.Log
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.steampoweredplayer.AppSettings
import app.steampoweredplayer.Library
import app.steampoweredplayer.data.GameEntry
import app.steampoweredplayer.data.LibraryIndexStatus
import app.steampoweredplayer.data.LibraryLayout
import app.steampoweredplayer.data.LibrarySnapshot
import app.steampoweredplayer.data.LibrarySource
import app.steampoweredplayer.data.RecordingEntry
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.data.chooseEndpoint
import app.steampoweredplayer.net.LinkMonitor
import app.steampoweredplayer.smb.SmbClient
import java.io.IOException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.runInterruptible
import kotlinx.coroutines.withContext

/** A game's row of the library: newest recording first. */
data class GameRow(val game: GameEntry, val recordings: List<RecordingEntry>) {
    val newest get() = recordings.first().startUtc
    val totalSeconds get() = recordings.sumOf { it.durationSeconds }
}

data class LibraryUi(
    val profile: ServerProfile? = null,
    val profiles: List<ServerProfile> = emptyList(),
    val source: LibrarySource = LibrarySource.None,
    val status: LibraryIndexStatus = LibraryIndexStatus.Missing,
    val layout: LibraryLayout? = null,
    val games: List<GameRow> = emptyList(),
    val refreshing: Boolean = false,
    val message: String? = null,
    val artVersion: Int = 0,
)

/** The library screen's state: the first reachable profile, the index, and change notify on the share, which replaces polling. */
class LibraryViewModel(app: Application) : AndroidViewModel(app) {
    private val settings = AppSettings(app)
    private val _ui = MutableStateFlow(LibraryUi())
    val ui: StateFlow<LibraryUi> = _ui
    private var library: Library? = null
    private var watcher: Job? = null
    private var refreshJob: Job? = null
    private val link = LinkMonitor(app)

    init {
        viewModelScope.launch { link.changes.collect { refresh() } }
        reload()
    }

    /** Reads the saved profiles again and opens the active one. Screens call this after the servers changed. */
    fun reload() {
        viewModelScope.launch {
            val profiles = withContext(Dispatchers.IO) { Library.profiles(getApplication()) }
            _ui.update { it.copy(profiles = profiles) }
            if (profiles.isEmpty()) {
                library = null
                watcher?.cancel()
                _ui.value = LibraryUi()
                return@launch
            }
            val preferred = profiles.firstOrNull { it.id == settings.activeProfileId }
            val chosen = withContext(Dispatchers.IO) {
                preferred?.takeIf { SmbClient.reachable(it) } ?: chooseEndpoint(profiles) { SmbClient.reachable(it) } ?: preferred ?: profiles.first()
            }
            open(chosen)
        }
    }

    fun open(profile: ServerProfile) {
        settings.activeProfileId = profile.id
        val lib = Library(getApplication(), profile)
        if (library?.profile?.id != profile.id) watcher?.cancel()                     // change notify follows the server in use
        library = lib
        _ui.update { it.copy(profile = profile) }
        viewModelScope.launch(Dispatchers.IO) { lib.index()?.cached()?.let(::publish) }
        refresh()
    }

    fun refresh() {
        val lib = library ?: return
        if (refreshJob?.isActive == true) return
        refreshJob = viewModelScope.launch {
            _ui.update { it.copy(refreshing = true) }
            val index = withContext(Dispatchers.IO) { lib.index() }                   // opens the vault and makes the SMB context
            if (index == null) {
                _ui.update { it.copy(refreshing = false, message = "No password is saved for this server.") }
                return@launch
            }
            val snapshot = withContext(Dispatchers.IO) { index.refresh() }
            publish(snapshot)
            _ui.update { it.copy(refreshing = false) }
            val layout = snapshot.layout
            val found = snapshot.index
            if (found != null && layout != null) {
                val profile = withContext(Dispatchers.IO) { Library.rememberLibraryId(getApplication(), lib.profile, found.libraryId) }
                if (profile != lib.profile) {
                    library = Library(getApplication(), profile)
                    _ui.update { it.copy(profile = profile) }
                }
                launch(Dispatchers.IO) {
                    library?.fetchArt(found, layout)
                    _ui.update { it.copy(artVersion = it.artVersion + 1) }
                }
                watch(layout)
            }
        }
    }

    /** Change notify on the data folder, or on the recordings when the desktop has never run. A failure waits for the next network change or a calm minute. */
    private fun watch(layout: LibraryLayout) {
        if (watcher?.isActive == true) return
        val current = library ?: return
        val folder = layout.dataFolder ?: layout.recordingRoot
        val recursive = layout.dataFolder == null
        watcher = viewModelScope.launch(Dispatchers.IO) {
            val remote = current.remote ?: return@launch
            while (true) {
                try {
                    runInterruptible { remote.awaitChange(folder, recursive) }
                    Log.i(TAG, "the library changed")
                    delay(SETTLE_MILLIS)                                              // the desktop writes the index, then the art
                    refresh()
                } catch (e: IOException) {
                    Log.w(TAG, "watch stopped: ${e.javaClass.simpleName}")
                    delay(RETRY_MILLIS)
                }
            }
        }
    }

    private fun publish(snapshot: LibrarySnapshot) {
        val index = snapshot.index
        val rows = if (index == null) {
            emptyList()
        } else {
            val byId = index.games.associateBy { it.gameId }
            val unknown = GameEntry("", "Unknown game")
            index.recordings.groupBy { it.gameId }.map { (id, list) ->
                GameRow(byId[id] ?: id?.let { GameEntry(it, "App $it") } ?: unknown, list.sortedByDescending { it.startUtc })
            }.sortedByDescending { it.newest }
        }
        _ui.update {
            it.copy(source = snapshot.source, status = snapshot.status, layout = snapshot.layout ?: it.layout, games = rows.ifEmpty { it.games }, message = snapshot.message)
        }
    }

    override fun onCleared() {
        link.close()
    }

    private companion object {
        const val TAG = "SppLibrary"
        const val SETTLE_MILLIS = 2_000L
        const val RETRY_MILLIS = 60_000L
    }
}
