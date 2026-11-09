// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.NavigationBar
import androidx.compose.material3.NavigationBarItem
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateListOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.runtime.collectAsState
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.lifecycle.viewmodel.compose.viewModel
import app.steampoweredplayer.AppSettings
import app.steampoweredplayer.data.RecordingEntry
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.export.Exports
import app.steampoweredplayer.ui.theme.Brand

enum class Tab(val label: String, val emoji: String) { Library("Library", "🎮"), Downloads("Downloads", "⬇️"), Exports("Exports", "🎬"), Settings("Settings", "⚙️") }

/** A screen above the tabs. */
sealed interface Route {
    data class Game(val row: GameRow) : Route
    data class Player(val entry: RecordingEntry, val title: String) : Route
}

/** A request to open a recording from outside, for the debug build's deep link. */
data class OpenRequest(val relativeFolder: String, val action: String?)

@Composable
fun AppRoot(openRequest: OpenRequest? = null, onOpenRequestHandled: () -> Unit = {}) {
    val library: LibraryViewModel = viewModel()
    val servers: ServersViewModel = viewModel()
    val ui by library.ui.collectAsState()
    var tab by remember { mutableStateOf(Tab.Library) }
    val stack = remember { mutableStateListOf<Route>() }
    val context = LocalContext.current

    // The share sheet for an export that asked for it. It carries the file of the tier the user chose.
    LaunchedEffect(Unit) {
        Exports.shareRequests.collect { record ->
            val uri = Uri.parse(record.uri ?: return@collect)
            val send = Intent(Intent.ACTION_SEND).setType("video/mp4").putExtra(Intent.EXTRA_STREAM, uri).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            context.startActivity(Intent.createChooser(send, record.displayName))
        }
    }

    // The notification permission, asked once, when the first thing that shows progress is there to ask for.
    val settings = remember { AppSettings(context) }
    val ask = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { settings.notificationsAsked = true }
    val askForNotifications = {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU && !settings.notificationsAsked &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) != PackageManager.PERMISSION_GRANTED
        ) {
            ask.launch(Manifest.permission.POST_NOTIFICATIONS)
        }
    }

    LaunchedEffect(openRequest, ui.games) {
        val request = openRequest ?: return@LaunchedEffect
        val rows = ui.games
        val hit = rows.firstNotNullOfOrNull { row -> row.recordings.firstOrNull { it.relativeFolder == request.relativeFolder }?.let { row to it } }
        if (hit != null) {
            stack.clear()
            stack.add(Route.Game(hit.first))
            stack.add(Route.Player(hit.second, hit.second.title ?: hit.first.game.name))
            onOpenRequestHandled()
        }
    }

    val top = stack.lastOrNull()
    BackHandler(enabled = top != null) { stack.removeAt(stack.lastIndex) }

    when (top) {
        is Route.Player -> {
            val profile: ServerProfile? = ui.profile
            if (profile == null) {
                stack.removeAt(stack.lastIndex)
            } else {
                PlayerScreen(profile, top.entry, top.title, ui, askForNotifications) { stack.removeAt(stack.lastIndex) }
            }
        }
        is Route.Game -> GameScreen(top.row, ui, library, onBack = { stack.removeAt(stack.lastIndex) }) { entry, title -> stack.add(Route.Player(entry, title)) }
        null -> Scaffold(
            containerColor = MaterialTheme.colorScheme.background,
            bottomBar = {
                NavigationBar(containerColor = MaterialTheme.colorScheme.surfaceContainer) {
                    Tab.entries.forEach { t ->
                        NavigationBarItem(selected = t == tab, onClick = { tab = t }, icon = { Text(t.emoji) }, label = { Text(t.label) })
                    }
                }
            },
        ) { padding ->
            Box(Modifier.fillMaxSize().padding(padding)) {
                when (tab) {
                    Tab.Library -> LibraryScreen(ui, library, onOpenGame = { stack.add(Route.Game(it)) }, onAddServer = {
                        servers.add()
                        tab = Tab.Settings
                    })
                    Tab.Downloads -> DownloadsScreen(ui)
                    Tab.Exports -> ExportsScreen()
                    Tab.Settings -> SettingsScreen(library)
                }
            }
        }
    }
}

/** The brand's page header. */
@Composable
fun BrandHeader(title: String, subtitle: String? = null, modifier: Modifier = Modifier, trailing: @Composable () -> Unit = {}) {
    Row(
        modifier.fillMaxWidth().background(Brand.Gradient, MaterialTheme.shapes.large).padding(horizontal = 20.dp, vertical = 16.dp),
        horizontalArrangement = Arrangement.SpaceBetween,
    ) {
        Column(Modifier.weight(1f)) {
            Text(title, color = Color.White, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold, maxLines = 2)
            if (subtitle != null) Text(subtitle, color = Color.White.copy(alpha = 0.85f), style = MaterialTheme.typography.bodySmall)
        }
        trailing()
    }
}

@Composable
fun SppPanel(modifier: Modifier = Modifier, content: @Composable () -> Unit) {
    Card(modifier.fillMaxWidth(), colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer), shape = MaterialTheme.shapes.large) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) { content() }
    }
}
