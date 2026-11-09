// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.setValue
import app.steampoweredplayer.ui.AppRoot
import app.steampoweredplayer.ui.OpenRequest
import app.steampoweredplayer.ui.theme.SppTheme

class MainActivity : ComponentActivity() {
    private var openRequest by mutableStateOf<OpenRequest?>(null)

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        openRequest = requestIn(intent)
        setContent { SppTheme { AppRoot(openRequest) { openRequest = null } } }
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        openRequest = requestIn(intent)
    }

    /** `spp://recording/<relative folder>` opens a recording. Only the debug build declares the filter for it, for driving the app in checks. */
    private fun requestIn(intent: Intent?): OpenRequest? {
        val uri = intent?.data ?: return null
        if (uri.scheme != "spp" || uri.host != "recording") return null
        return OpenRequest(uri.path.orEmpty().trim('/'), uri.getQueryParameter("action"))
    }
}
