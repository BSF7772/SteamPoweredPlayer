// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.IOException

/** A share of a server, and where in it the recordings are. [recordingsPath] is null when none were found; empty means the share's root. */
data class FoundShare(val name: String, val recordingsPath: String?) {
    val hasRecordings get() = recordingsPath != null
}

/** Finding the recordings from a server's address alone, so adding a server doesn't ask for a share or a folder. Read-only. Blocking: call off the main thread. */
object ShareScan {
    private const val STEAM_RECORDING_FILE = "gamerecording.pb"
    private const val CLIPS = "clips"
    private const val VIDEO = "video"
    private const val MAX_DEPTH = 6
    private const val MAX_FOLDERS_PER_SHARE = 200
    private val clipFolder = Regex("^clip_\\d+_\\d{8}_\\d{6}$")
    private val cacheFolder = Regex("^bg_\\d+_\\d{8}_\\d{6}$")
    private val notWorthEntering = setOf("appdata", "windows", "program files", "program files (x86)", "programdata", "system volume information", "\$recycle.bin", "node_modules")

    /** The shares a person would mean: no administrative ones (`C$`, `IPC$`), each once, in name order. */
    fun visible(names: List<String>): List<String> = names.map { it.trim().trimEnd('/', '\\') }
        .filter { it.isNotEmpty() && !it.endsWith('$') }
        .distinctBy { it.lowercase() }
        .sortedBy { it.lowercase() }

    /** The share to select at first: the only one with recordings, else the first with recordings. Null when none has any. */
    fun preferred(found: List<FoundShare>): FoundShare? = found.firstOrNull { it.hasRecordings }

    /**
     * Breadth first, so the shallowest library wins, and bounded, so a big share doesn't stall the form. A folder that can't be read is skipped. A folder counts only when it
     * holds Steam's own names: `video` and `clips` are common words on a media share.
     */
    fun findRecordings(files: RemoteFiles, keepGoing: () -> Boolean = { true }): String? {
        val queue = ArrayDeque<Pair<String, Int>>()
        queue.add("" to 0)
        var visited = 0
        while (queue.isNotEmpty() && visited < MAX_FOLDERS_PER_SHARE && keepGoing()) {
            val (folder, depth) = queue.removeFirst()
            visited++
            val names = namesIn(files, folder) ?: continue
            if (isRecordingRoot(files, folder, names)) return folder
            if (depth >= MAX_DEPTH) continue
            names.filter(::worthEntering).forEach { queue.add(LibraryLocator.join(folder, it) to depth + 1) }
        }
        return null
    }

    private fun isRecordingRoot(files: RemoteFiles, folder: String, names: Set<String>): Boolean {
        if (STEAM_RECORDING_FILE in names) return true
        if (CLIPS in names && namesIn(files, LibraryLocator.join(folder, CLIPS))?.any(clipFolder::matches) == true) return true
        return VIDEO in names && namesIn(files, LibraryLocator.join(folder, VIDEO))?.any(cacheFolder::matches) == true
    }

    private fun worthEntering(name: String) = !name.startsWith('.') && !name.endsWith('$') && !name.contains('.') && name.lowercase() !in notWorthEntering

    private fun namesIn(files: RemoteFiles, folder: String): Set<String>? = try {
        files.list(folder).map { it.trimEnd('/') }.toSet()
    } catch (_: IOException) {
        null                                                                          // RemoteFileGone is an IOException too: gone, or no access
    }
}
