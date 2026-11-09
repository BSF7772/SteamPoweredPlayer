// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer

import android.content.Intent
import android.content.pm.ActivityInfo
import android.graphics.Color
import android.os.Build
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.SystemBarStyle
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
        // Our bars draw behind the system's: no scrim, or three-button navigation gets a darker strip of its own under the app's navigation bar.
        val clear = SystemBarStyle.auto(Color.TRANSPARENT, Color.TRANSPARENT)
        enableEdgeToEdge(statusBarStyle = clear, navigationBarStyle = clear)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) window.isNavigationBarContrastEnforced = false
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
        when (uri.getQueryParameter("orientation")) {
            "landscape" -> requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_LANDSCAPE
            "portrait" -> requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_PORTRAIT
        }
        return OpenRequest(uri.path.orEmpty().trim('/'), uri.getQueryParameter("action"))
    }
}
