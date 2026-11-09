// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.foundation.layout.Column
import androidx.compose.material3.FilterChip
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.steampoweredplayer.AppSettings
import app.steampoweredplayer.play.DecodeMode

/** Servers, the decoder, and the hidden colour switch. */
@Composable
fun SettingsScreen(library: LibraryViewModel) {
    val context = LocalContext.current
    val settings = remember { AppSettings(context) }
    val servers: ServersViewModel = viewModel()
    val serverState by servers.state.collectAsState()
    val libraryUi by library.ui.collectAsState()
    LaunchedEffect(serverState.profiles.map { it.id }) { library.reload() }

    var decode by remember { mutableStateOf(settings.decodeMode) }
    var fast by remember { mutableStateOf(settings.fastReencode) }
    var taps by remember { mutableIntStateOf(0) }

    Column(
        Modifier.fillMaxSize().safeDrawingPadding().verticalScroll(rememberScrollState()).padding(16.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        BrandHeader("Settings", libraryUi.profile?.let { "Connected through ${it.name}" })
        ServersSection(servers)
        if (libraryUi.profiles.size > 1) {
            SppPanel {
                Text("Active server", style = MaterialTheme.typography.titleSmall)
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    libraryUi.profiles.forEach { p -> FilterChip(libraryUi.profile?.id == p.id, { library.open(p) }, { Text(p.name) }) }
                }
            }
        }
        SppPanel {
            Text("Playback decoder", style = MaterialTheme.typography.titleSmall)
            Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                FilterChip(decode == DecodeMode.Gpu, {
                    decode = DecodeMode.Gpu
                    settings.decodeMode = DecodeMode.Gpu
                }, { Text("GPU (hardware)") })
                FilterChip(decode == DecodeMode.Cpu, {
                    decode = DecodeMode.Cpu
                    settings.decodeMode = DecodeMode.Cpu
                }, { Text("CPU") })
            }
            Text("Applies to the next recording you open.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        }
        SppPanel {
            Text("About", style = MaterialTheme.typography.titleSmall)
            val version = remember { context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "" }
            Text(
                "SteamPoweredPlayer $version · GPL-3.0-or-later. mpv, FFmpeg, Media3, JCIFS and Coil keep their own licences.",
                Modifier.clickable { taps++ },
                style = MaterialTheme.typography.bodySmall,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (taps >= HIDDEN_TAPS || fast) {
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text("Fast re-encode: hardware decode, colours may clip", Modifier.weight(1f), style = MaterialTheme.typography.bodySmall)
                    Switch(fast, {
                        fast = it
                        settings.fastReencode = it
                    })
                }
            }
        }
    }
}

private const val HIDDEN_TAPS = 7
