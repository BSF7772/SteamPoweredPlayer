// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer

import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.enableEdgeToEdge
import app.steampoweredplayer.ui.HomeScreen
import app.steampoweredplayer.ui.theme.SppTheme

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        enableEdgeToEdge()
        setContent { SppTheme { HomeScreen() } }
    }
}
