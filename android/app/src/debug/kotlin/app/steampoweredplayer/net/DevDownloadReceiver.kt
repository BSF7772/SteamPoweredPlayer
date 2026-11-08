// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.net

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import app.steampoweredplayer.data.ProfileStore
import java.io.File

/**
 * Debug builds only: lets `adb shell am broadcast` start or cancel a download with the credentials saved in the app, until the app has its
 * downloads screen. Guarded by the DUMP permission, which adb's shell holds and ordinary apps can't get. Extras: folder (required), recording
 * (default: the folder's last name), cancel (boolean).
 */
class DevDownloadReceiver : BroadcastReceiver() {
    override fun onReceive(context: Context, intent: Intent) {
        val folder = intent.getStringExtra("folder") ?: return
        val recording = intent.getStringExtra("recording") ?: folder.trimEnd('/', '\\').substringAfterLast('/').substringAfterLast('\\')
        if (intent.getBooleanExtra("cancel", false)) {
            Downloads.cancel(context, recording)
            return
        }
        val profile = ProfileStore(File(context.filesDir, "servers.json")).load().firstOrNull() ?: return
        Downloads.enqueue(context, DownloadRequest(profile.id, recording, folder.replace('\\', '/')))
    }
}
