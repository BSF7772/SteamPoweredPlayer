// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.smb

import app.steampoweredplayer.data.ChangeNotifier
import app.steampoweredplayer.data.RemoteFileGone
import app.steampoweredplayer.data.RemoteFiles
import app.steampoweredplayer.data.ServerProfile
import java.io.IOException
import java.io.InputStream
import org.codelibs.jcifs.smb.CIFSContext
import org.codelibs.jcifs.smb.CIFSException
import org.codelibs.jcifs.smb.FileNotifyInformation
import org.codelibs.jcifs.smb.impl.SmbFile

/** Reads a library over SMB, read-only, with read + write + delete sharing so Steam can keep writing and trimming. */
class SmbRemoteFiles(private val profile: ServerProfile, password: String) : RemoteFiles, ChangeNotifier {
    private val context: CIFSContext = SmbClient.context(profile, password)

    override fun list(relativeFolder: String): List<String> = guarded(relativeFolder) {
        SmbFile(folderUrl(relativeFolder), context).use { folder ->
            if (!folder.exists()) throw RemoteFileGone(relativeFolder)
            folder.list().map { it.trimEnd('/') }
        }
    }

    /** Blocks until the server reports a change. A cancelled coroutine interrupts the thread, which closes the handle. Read-only: this opens the folder to list it. */
    override fun awaitChange(relativeFolder: String, recursive: Boolean) = guarded(relativeFolder) {
        SmbFile(folderUrl(relativeFolder), context).use { folder ->
            folder.watch(CHANGES, recursive).use { it.watch() }
        }
        Unit
    }

    private fun folderUrl(relativeFolder: String) = if (relativeFolder.isEmpty()) profile.url() else profile.url(relativeFolder.trimEnd('/') + "/")

    override fun size(relativePath: String): Long = guarded(relativePath) {
        SmbFile(profile.url(relativePath), context).use { f ->
            if (!f.exists()) throw RemoteFileGone(relativePath)
            f.length()
        }
    }

    override fun open(relativePath: String, offset: Long): InputStream = guarded(relativePath) {
        val file = SmbFile(profile.url(relativePath), context)
        if (!file.exists()) {
            file.close()
            throw RemoteFileGone(relativePath)
        }
        val input = file.openInputStream(SHARE_READ_WRITE_DELETE)
        var left = offset
        while (left > 0) {
            val n = input.skip(left)
            if (n <= 0) throw IOException("couldn't skip to $offset")
            left -= n
        }
        input
    }

    private inline fun <T> guarded(path: String, block: () -> T): T = try {
        block()
    } catch (e: CIFSException) {
        throw IOException("smb error on ${path.substringAfterLast('/')}", e)
    }

    private companion object {
        const val SHARE_READ_WRITE_DELETE = 7
        const val CHANGES = FileNotifyInformation.FILE_NOTIFY_CHANGE_FILE_NAME or FileNotifyInformation.FILE_NOTIFY_CHANGE_DIR_NAME or FileNotifyInformation.FILE_NOTIFY_CHANGE_LAST_WRITE
    }
}
