// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.RadioButton
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.unit.dp
import app.steampoweredplayer.AppSettings
import app.steampoweredplayer.R
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.play.DecodeMode

/** Servers, the decoder, and the hidden colour switch. A plain settings list. */
@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun SettingsScreen(library: LibraryViewModel, servers: ServersViewModel, onAddServer: () -> Unit, onEditServer: (ServerProfile) -> Unit, onOpenDownloads: () -> Unit) {
    val context = LocalContext.current
    val settings = remember { AppSettings(context) }
    val serverState by servers.state.collectAsState()
    val libraryUi by library.ui.collectAsState()

    var decode by remember { mutableStateOf(settings.decodeMode) }
    var fast by remember { mutableStateOf(settings.fastReencode) }
    var taps by remember { mutableIntStateOf(0) }

    Column(Modifier.fillMaxSize()) {
        SppTopBar("Settings")
        Column(Modifier.weight(1f).verticalScroll(rememberScrollState())) {
            SppSectionTitle("Servers")
            serverState.profiles.forEach { profile ->
                val attempt = serverState.attempt?.takeIf { it.profileId == profile.id }
                val line = connectionLine(attempt)
                val active = libraryUi.profile?.id == profile.id
                SppRow(Modifier.padding(end = 4.dp)) {
                    SppRow(Modifier.weight(1f).selectable(active, role = Role.RadioButton) { library.open(profile) }.padding(start = 16.dp, top = 8.dp, bottom = 8.dp)) {
                        RadioButton(active, null)
                        Spacer(Modifier.width(16.dp))
                        SppRowText(
                            profile.name,
                            line?.first ?: "${profile.host} · ${profile.share}",
                            Modifier.weight(1f),
                            detailColor = if (line?.second == true) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    IconButton({ onEditServer(profile) }) { Icon(SppIcons.Edit, "Edit ${profile.name}", tint = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
            }
            SppRow(Modifier.clickable(onClick = onAddServer).height(56.dp).padding(horizontal = 16.dp)) {
                Icon(SppIcons.Add, null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(16.dp))
                Text(stringResource(R.string.add_server), style = MaterialTheme.typography.bodyLarge, color = MaterialTheme.colorScheme.primary)
            }

            SppSectionTitle("Playback")
            Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                SppRowText("Decoder", "Applies to the next recording you open.")
                Spacer(Modifier.height(12.dp))
                SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
                    listOf(DecodeMode.Gpu to "GPU (hardware)", DecodeMode.Cpu to "CPU").forEachIndexed { i, (mode, label) ->
                        SegmentedButton(decode == mode, {
                            decode = mode
                            settings.decodeMode = mode
                        }, SegmentedButtonDefaults.itemShape(i, 2)) { Text(label) }
                    }
                }
            }
            SppRowText(
                "Playback cache limit",
                "${settings.cacheGigabytes} GB. Change it on the Downloads tab.",
                Modifier.fillMaxWidth().clickable(onClick = onOpenDownloads).padding(horizontal = 16.dp, vertical = 12.dp),
            )

            SppSectionTitle("About")
            val version = remember { context.packageManager.getPackageInfo(context.packageName, 0).versionName ?: "" }
            Text(
                "SteamPoweredPlayer $version · GPL-3.0-or-later. mpv, FFmpeg, Media3, JCIFS and Coil keep their own licences.",
                Modifier.fillMaxWidth().clickable { taps++ }.padding(horizontal = 16.dp, vertical = 8.dp),
                style = MaterialTheme.typography.bodyMedium,
                color = MaterialTheme.colorScheme.onSurfaceVariant,
            )
            if (taps >= HIDDEN_TAPS || fast) {
                SppRow(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                    Text("Fast re-encode: hardware decode, colours may clip", Modifier.weight(1f).padding(end = 16.dp), style = MaterialTheme.typography.bodyMedium)
                    Switch(fast, {
                        fast = it
                        settings.fastReencode = it
                    })
                }
            }
            Spacer(Modifier.height(16.dp))
        }
    }
}

private const val HIDDEN_TAPS = 7
