// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import java.io.File
import java.io.IOException
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.Json

/** Download-first is the only behaviour until wired streaming is validated (plan S3b, S9); the choice is stored for then. */
@Serializable
enum class NetworkMode { Auto, AlwaysStream, AlwaysDownload }

/**
 * How to reach one library over SMB. The password is not here: it lives in the Keystore-backed vault, under [id]. Profiles are tried in
 * list order, so a LAN profile goes before a VPN one.
 */
@Serializable
data class ServerProfile(
    val id: String,
    val name: String,
    val host: String,
    val share: String,
    val path: String = "",
    val user: String = "",
    val domain: String = "",
    val port: Int = DEFAULT_PORT,
    val requireEncryption: Boolean = true,
    val networkMode: NetworkMode = NetworkMode.Auto,
    /** The library this profile last reached (`library.json`): profiles with the same id are the same library by other routes. */
    val libraryId: String? = null,
) {
    /** The problems that stop this profile being saved; empty when it's fine. */
    fun problems(): List<ProfileProblem> = buildList {
        if (name.isBlank()) add(ProfileProblem.NoName)
        if (host.isBlank() || host.any { it == '/' || it == '\\' || it.isWhitespace() } || host.contains("://")) add(ProfileProblem.BadHost)
        if (share.isBlank() || share.any { it == '/' || it == '\\' }) add(ProfileProblem.BadShare)
        if (path.split('/', '\\').any { it == ".." }) add(ProfileProblem.BadPath)
        if (port !in 1..65535) add(ProfileProblem.BadPort)
    }

    /** `smb://host[:port]/share/path/relative`, forward slashes, a folder ending in `/` when [relative] does. */
    fun url(relative: String = ""): String {
        val h = if (host.contains(':') && !host.startsWith("[")) "[$host]" else host
        val p = if (port == DEFAULT_PORT) "" else ":$port"
        val parts = (path.split('/', '\\') + relative.split('/', '\\')).filter { it.isNotEmpty() }
        val tail = if (relative.isEmpty() || relative.endsWith('/')) "/" else ""
        return "smb://$h$p/$share/" + parts.joinToString("/") + if (parts.isEmpty()) "" else tail
    }

    /** The same server, with paths counted from the share's root. Library layouts and download requests are relative to that. */
    fun atShareRoot(): ServerProfile = copy(path = "")

    companion object {
        const val DEFAULT_PORT = 445
    }
}

enum class ProfileProblem { NoName, BadHost, BadShare, BadPath, BadPort }

/** The first reachable profile, in list order, starting from none. [reachable] is the caller's short-timeout probe. */
inline fun chooseEndpoint(profiles: List<ServerProfile>, reachable: (ServerProfile) -> Boolean): ServerProfile? =
    profiles.firstOrNull { reachable(it) }

/** The saved profiles, in a private app file. A write replaces the file whole, so a crash leaves the old list or the new one. */
class ProfileStore(private val file: File) {
    private val json = Json { ignoreUnknownKeys = true; encodeDefaults = true; prettyPrint = true }

    /** A missing or unreadable file reads as no profiles: the vault's passwords stay and are overwritten when they're entered again. */
    fun load(): List<ServerProfile> = try {
        if (file.exists()) json.decodeFromString<List<ServerProfile>>(file.readText()) else emptyList()
    } catch (_: SerializationException) {
        emptyList()
    } catch (_: IOException) {
        emptyList()
    }

    fun save(profiles: List<ServerProfile>) {
        file.parentFile?.mkdirs()
        val tmp = File(file.path + ".tmp")
        tmp.writeText(json.encodeToString(profiles))
        if (!tmp.renameTo(file)) {           // a rename over an existing file can fail on some filesystems
            file.delete()
            if (!tmp.renameTo(file)) throw IOException("couldn't replace ${file.name}")
        }
    }
}
