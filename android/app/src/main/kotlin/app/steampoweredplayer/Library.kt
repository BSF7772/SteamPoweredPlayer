// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer

import android.content.Context
import app.steampoweredplayer.data.IndexRepository
import app.steampoweredplayer.data.LibraryIndex
import app.steampoweredplayer.data.LibraryLayout
import app.steampoweredplayer.data.LibraryLocator
import app.steampoweredplayer.data.ProfileStore
import app.steampoweredplayer.data.RecordingEntry
import app.steampoweredplayer.data.RecordingRepository
import app.steampoweredplayer.data.RemoteFileGone
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.smb.PasswordVault
import app.steampoweredplayer.smb.SmbRemoteFiles
import java.io.File
import java.io.IOException

/** Where one server's library lives on the phone, and how to reach it. */
class Library(context: Context, val profile: ServerProfile) {
    private val app = context.applicationContext
    private val dir = File(app.filesDir, "library/${profile.id}")

    /** The share, read-only, with paths from its root. Null without a saved password. */
    val remote: SmbRemoteFiles? by lazy {
        PasswordVault(File(app.noBackupFilesDir, "vault")).get(profile.id)?.let { SmbRemoteFiles(profile.atShareRoot(), it) }
    }

    fun index(): IndexRepository? = remote?.let { IndexRepository(dir, it, profile.path) }

    /** Works without a connection for what's stored. */
    fun recordings(layout: LibraryLayout? = null): RecordingRepository = RecordingRepository(File(dir, "recordings"), remote, layout)

    fun artFile(gameId: String, relative: String): File = File(File(dir, "art"), relative.substringAfter("art/").also { require(gameId.isNotEmpty()) })

    /** Fetches each game's picture once. Failures just leave the game without one. Blocking. */
    fun fetchArt(index: LibraryIndex, layout: LibraryLayout) {
        val files = remote ?: return
        val data = layout.dataFolder ?: return
        for (game in index.games) {
            val art = game.art ?: continue
            val path = art.cover ?: art.hero ?: art.logo ?: continue
            if (path.split('/').any { it == ".." }) continue
            val target = artFile(game.gameId, path)
            if (target.exists()) continue
            try {
                val bytes = files.open(LibraryLocator.join(data, path), 0).use { it.readBytes() }
                target.parentFile?.mkdirs()
                val tmp = File(target.path + ".tmp")
                tmp.writeBytes(bytes)
                tmp.renameTo(target)
            } catch (_: RemoteFileGone) {
                continue
            } catch (_: IOException) {
                return
            }
        }
    }

    companion object {
        fun profiles(context: Context): List<ServerProfile> = ProfileStore(File(context.applicationContext.filesDir, "servers.json")).load()

        /** Remembers which library a profile reaches, so other profiles of it become failover routes and the cache keeps its files under its id. */
        fun rememberLibraryId(context: Context, profile: ServerProfile, libraryId: String): ServerProfile {
            if (libraryId.isEmpty() || profile.libraryId == libraryId || !app.steampoweredplayer.data.isPlainName(libraryId)) return profile
            val store = ProfileStore(File(context.applicationContext.filesDir, "servers.json"))
            val updated = profile.copy(libraryId = libraryId)
            store.save(store.load().map { if (it.id == profile.id) updated else it })
            return updated
        }
    }
}

/** A saved clip's name, or the game's and its start time. */
fun RecordingEntry.displayTitle(gameName: String?): String = title ?: gameName ?: "Recording"
