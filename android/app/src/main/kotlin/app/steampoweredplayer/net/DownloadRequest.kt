// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.net

import app.steampoweredplayer.data.ChunkScan
import app.steampoweredplayer.data.HeadCheck
import app.steampoweredplayer.data.ServerProfile
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json

/** One download job. [relativeFolder] is the media folder relative to the share's root. No [chunks] means the whole recording. */
@Serializable
data class DownloadRequest(
    val profileId: String,
    val recording: String,
    val relativeFolder: String,
    val chunks: List<Int>? = null,
    val playheadChunk: Int = 1,
) {
    fun encode(): String = json.encodeToString(this)

    companion object {
        private val json = Json { ignoreUnknownKeys = true }

        fun decode(text: String): DownloadRequest = json.decodeFromString(text)
    }
}

/** Pure helpers of the download job. */
object DownloadRoutes {
    /**
     * The profiles to read from, in failover order: the requested one first, then the others that reached the same library by another
     * route (LAN, VPN). Null when the requested profile no longer exists.
     */
    fun sources(requestedId: String, profiles: List<ServerProfile>): List<ServerProfile>? {
        val requested = profiles.firstOrNull { it.id == requestedId } ?: return null
        val library = requested.libraryId ?: return listOf(requested)
        return listOf(requested) + profiles.filter { it.id != requestedId && it.libraryId == library }
    }

    /** What names the library in the cache: the id it reports once known, the profile's own id until then. */
    fun libraryKey(profile: ServerProfile): String = profile.libraryId ?: profile.id

    /**
     * The chunk numbers complete in both streams, sorted, by the desktop's rule: a `.tmp` is Steam still writing, a chunk in one stream can't play yet, and a crash can
     * leave a zero-filled file under a normal name. [head] reads a file's head; only files that pass the name checks are read.
     */
    fun completeChunks(fileNames: Iterable<String>, head: (String) -> HeadCheck = { HeadCheck.Valid }): List<Int> = ChunkScan.complete(fileNames, head)
}
