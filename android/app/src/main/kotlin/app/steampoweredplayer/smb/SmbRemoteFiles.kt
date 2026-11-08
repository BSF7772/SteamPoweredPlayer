// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.smb

import app.steampoweredplayer.data.RemoteFileGone
import app.steampoweredplayer.data.RemoteFiles
import app.steampoweredplayer.data.ServerProfile
import java.io.IOException
import java.io.InputStream
import org.codelibs.jcifs.smb.CIFSContext
import org.codelibs.jcifs.smb.CIFSException
import org.codelibs.jcifs.smb.impl.SmbFile

/** Reads a library over SMB, read-only, with read + write + delete sharing so Steam can keep writing and trimming. */
class SmbRemoteFiles(private val profile: ServerProfile, password: String) : RemoteFiles {
    private val context: CIFSContext = SmbClient.context(profile, password)

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
    }
}
