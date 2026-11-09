// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.text.format.DateFormat
import android.text.format.Formatter
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.tween
import androidx.compose.foundation.ExperimentalFoundationApi
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.combinedClickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedCard
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.draw.clip
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.steampoweredplayer.Library
import app.steampoweredplayer.R
import app.steampoweredplayer.data.ConnectionPhase
import app.steampoweredplayer.data.LibrarySource
import app.steampoweredplayer.data.RecordingEntry
import app.steampoweredplayer.data.RecordingKind
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.data.key
import app.steampoweredplayer.net.DownloadRoutes
import app.steampoweredplayer.net.Downloads
import app.steampoweredplayer.play.TimeText
import app.steampoweredplayer.ui.theme.Brand
import coil3.compose.AsyncImage
import java.io.File
import java.util.Date
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.withContext

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun LibraryScreen(ui: LibraryUi, vm: LibraryViewModel, onOpenGame: (GameRow) -> Unit, onAddServer: () -> Unit) {
    val context = LocalContext.current
    var choosing by remember { mutableStateOf(false) }
    val connecting = ui.profile != null && ui.connection == ConnectionPhase.Connecting

    Column(Modifier.fillMaxSize()) {
        // Static: the title. Everything that changes looks like what it is, something to press.
        SppTopBar("SteamPoweredPlayer", actions = {
            if (ui.profile != null) {
                IconButton({ vm.refresh(fromUser = true) }, enabled = !ui.refreshing) {
                    if (ui.refreshing && !connecting) CircularProgressIndicator(Modifier.size(22.dp), strokeWidth = 2.dp) else Icon(SppIcons.Refresh, "Refresh library")
                }
            }
        })
        if (ui.profile != null) ServerChip(ui, Modifier.padding(start = 16.dp, end = 16.dp, bottom = 8.dp)) { choosing = true }
        Box(Modifier.weight(1f)) {
            LazyVerticalGrid(
                columns = GridCells.Adaptive(150.dp),
                modifier = Modifier.fillMaxSize(),
                contentPadding = PaddingValues(start = 16.dp, end = 16.dp, top = 8.dp, bottom = 16.dp),
                horizontalArrangement = Arrangement.spacedBy(12.dp),
                verticalArrangement = Arrangement.spacedBy(12.dp),
            ) {
                item(span = { GridItemSpan(maxLineSpan) }) {
                    Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                        val notice by Downloads.meteredNotice.collectAsState()
                        if (notice) MeteredNotice()
                        if (ui.connection == ConnectionPhase.Failed && ui.profile != null) ConnectionFailed(ui.profile.name, onChange = { choosing = true }, onRetry = { vm.refresh(fromUser = true) })
                        if (ui.source == LibrarySource.Scan) {
                            Text("The desktop app hasn't indexed this share: ids instead of names, and no art.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        ui.message?.let { Text(it, style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.error) }
                        if (ui.refreshing && ui.games.isEmpty() && !connecting) LinearProgressIndicator(Modifier.fillMaxWidth())
                    }
                }
                if (ui.profile == null) {
                    item(span = { GridItemSpan(maxLineSpan) }) {
                        SppPanel {
                            Text(stringResource(R.string.home_empty_title), style = MaterialTheme.typography.titleMedium)
                            Text(stringResource(R.string.home_empty_body), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                            Button(onAddServer) { Text(stringResource(R.string.add_server)) }
                        }
                    }
                } else if (ui.games.isEmpty() && !ui.refreshing && ui.connection == ConnectionPhase.Connected) {
                    item(span = { GridItemSpan(maxLineSpan) }) {
                        SppPanel { Text("No recordings here yet.", style = MaterialTheme.typography.bodyMedium) }
                    }
                }
                items(ui.games, key = { it.game.gameId + it.game.name }) { row ->
                    val index = ui.games.indexOf(row)
                    val fade = remember { Animatable(0f) }
                    LaunchedEffect(Unit) {
                        delay((index.coerceAtMost(12) * STAGGER_MILLIS).toLong())                // the brand's staggered entrance
                        fade.animateTo(1f, tween(FADE_MILLIS))
                    }
                    val art = remember(ui.artVersion, row.game.gameId, ui.profile, ui.profiles) { artFor(context, ui, row) }
                    Card(
                        Modifier.fillMaxWidth().alpha(fade.value * if (connecting) CONNECTING_ALPHA else 1f).clip(MaterialTheme.shapes.medium).clickable { onOpenGame(row) },
                        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
                        shape = MaterialTheme.shapes.medium,
                    ) {
                        Box(Modifier.fillMaxWidth().aspectRatio(0.72f).background(Brand.Gradient), contentAlignment = Alignment.Center) {
                            if (art != null) {
                                AsyncImage(art, row.game.name, Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
                            } else {
                                Icon(SppIcons.Library, null, Modifier.size(40.dp), tint = Color.White)
                            }
                        }
                        Column(Modifier.padding(10.dp)) {
                            Text(row.game.name, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            Text(
                                "${countOf(row.recordings.size)} · ${TimeText.of(row.totalSeconds)}",
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 1,
                                overflow = TextOverflow.Ellipsis,
                            )
                        }
                    }
                }
            }
            if (connecting) {
                Surface(Modifier.align(Alignment.TopCenter).padding(top = 16.dp).size(48.dp), shape = CircleShape, color = MaterialTheme.colorScheme.surfaceContainerHighest, shadowElevation = 4.dp) {
                    Box(contentAlignment = Alignment.Center) { CircularProgressIndicator(Modifier.size(32.dp), strokeWidth = 3.dp) }
                }
            }
        }
    }

    if (choosing) {
        ModalBottomSheet({ choosing = false }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = MaterialTheme.colorScheme.surfaceContainer) {
            Text("Server", Modifier.padding(horizontal = 24.dp, vertical = 8.dp), style = MaterialTheme.typography.titleMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            ui.profiles.forEach { p: ServerProfile ->
                val selected = ui.profile?.id == p.id
                Row(
                    Modifier.fillMaxWidth().selectable(selected, role = Role.RadioButton) {
                        choosing = false
                        if (!selected) vm.open(p)
                    }.padding(horizontal = 24.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    RadioButton(selected, null)
                    Spacer(Modifier.width(16.dp))
                    SppRowText(p.name, p.host, Modifier.weight(1f))
                }
            }
            Row(
                Modifier.fillMaxWidth().clickable {
                    choosing = false
                    onAddServer()
                }.height(56.dp).padding(horizontal = 24.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(SppIcons.Add, null, tint = MaterialTheme.colorScheme.primary)
                Spacer(Modifier.width(16.dp))
                Text(stringResource(R.string.add_server), color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.bodyLarge)
            }
            Spacer(Modifier.height(16.dp))
        }
    }
}

/** The dynamic part of the header: which server, how it's doing, and that it can be changed. */
@Composable
private fun ServerChip(ui: LibraryUi, modifier: Modifier, onClick: () -> Unit) {
    val failed = ui.connection == ConnectionPhase.Failed
    OutlinedCard(
        onClick,
        modifier.height(40.dp),
        shape = MaterialTheme.shapes.medium,
        colors = CardDefaults.outlinedCardColors(containerColor = if (failed) MaterialTheme.colorScheme.errorContainer else MaterialTheme.colorScheme.surfaceContainer),
    ) {
        Row(Modifier.fillMaxHeight().padding(start = 12.dp, end = 8.dp), verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            when (ui.connection) {
                ConnectionPhase.Connecting -> CircularProgressIndicator(Modifier.size(18.dp), strokeWidth = 2.dp)
                ConnectionPhase.Connected -> Box(Modifier.size(10.dp).background(Color(0xFF4ADE80), CircleShape))
                ConnectionPhase.Failed -> Icon(SppIcons.Error, null, Modifier.size(18.dp), tint = MaterialTheme.colorScheme.error)
            }
            Text(ui.profile?.name.orEmpty(), Modifier.weight(1f, fill = false), style = MaterialTheme.typography.labelLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(statusOf(ui), style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant, maxLines = 1)
            Icon(SppIcons.ExpandMore, null, Modifier.size(20.dp), tint = MaterialTheme.colorScheme.onSurfaceVariant)
        }
    }
}

@Composable
private fun ConnectionFailed(server: String, onChange: () -> Unit, onRetry: () -> Unit) {
    Surface(color = MaterialTheme.colorScheme.errorContainer, contentColor = MaterialTheme.colorScheme.onErrorContainer, shape = MaterialTheme.shapes.medium) {
        Column(Modifier.fillMaxWidth().padding(start = 16.dp, end = 8.dp, top = 12.dp, bottom = 4.dp)) {
            Text("Can't reach $server", style = MaterialTheme.typography.titleSmall)
            Text("Showing your library as last seen. Check the server is on and the network is connected.", style = MaterialTheme.typography.bodyMedium)
            Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.End) {
                TextButton(onChange) { Text("Change server", color = MaterialTheme.colorScheme.onErrorContainer) }
                TextButton(onRetry) { Text("Retry", color = MaterialTheme.colorScheme.onErrorContainer) }
            }
        }
    }
}

private fun statusOf(ui: LibraryUi) = when (ui.connection) {
    ConnectionPhase.Connecting -> "Connecting…"
    ConnectionPhase.Failed -> "Not connected"
    ConnectionPhase.Connected -> when (ui.source) {
        LibrarySource.Scan -> "Scanned"
        else -> "Up to date"
    }
}

private fun countOf(recordings: Int) = if (recordings == 1) "1 recording" else "$recordings recordings"

/** A game's picture, from any of the routes to this library: switching from Wi-Fi to Tailscale shouldn't lose the art. */
private fun artFor(context: android.content.Context, ui: LibraryUi, row: GameRow): File? {
    val art = row.game.art ?: return null
    val path = art.cover ?: art.hero ?: art.logo ?: return null
    val current = ui.profile ?: return null
    val routes = listOf(current) + ui.profiles.filter { it.id != current.id && (current.libraryId == null || it.libraryId == null || it.libraryId == current.libraryId) }
    return routes.asSequence().map { Library(context, it).artFile(row.game.gameId, path) }.firstOrNull { it.exists() }
}

@Composable
fun MeteredNotice() {
    val context = LocalContext.current
    SppPanel {
        Text(stringResource(R.string.metered_notice), style = MaterialTheme.typography.bodyMedium)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            TextButton({ Downloads.closeMeteredNotice(context, forever = false) }) { Text(stringResource(R.string.ok)) }
            TextButton({ Downloads.closeMeteredNotice(context, forever = true) }) { Text(stringResource(R.string.dont_show_again)) }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class, ExperimentalFoundationApi::class)
@Composable
fun GameScreen(row: GameRow, ui: LibraryUi, vm: LibraryViewModel, onBack: () -> Unit, onOpen: (RecordingEntry, String, Boolean) -> Unit) {
    val context = LocalContext.current
    val current = ui.games.firstOrNull { it.game.gameId == row.game.gameId } ?: row
    val profile = ui.profile
    val libraryKey = profile?.let { DownloadRoutes.libraryKey(it) }
    val here by produceState(emptyMap<String, app.steampoweredplayer.data.LocalChunkCache.CachedRecording>(), libraryKey, ui.keptVersion) {
        value = withContext(Dispatchers.IO) {
            val cache = Downloads.cache(context)
            cache.recordings().filter { it.libraryId == libraryKey }.associateBy { it.recording }
        }
    }
    var menu by remember { mutableStateOf<RecordingEntry?>(null) }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        contentWindowInsets = WindowInsets(0),
        topBar = { SppTopBar(current.game.name, "${countOf(current.recordings.size)} · ${TimeText.of(current.totalSeconds)}", onBack = onBack) },
    ) { padding ->
        LazyColumn(Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding).navigationBarsPadding()) {
            items(current.recordings, key = { it.relativeFolder }) { entry ->
                val cached = here[entry.key]
                val total = entry.runs.sumOf { it.count }
                val title = entry.title ?: current.game.name
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Row(
                        Modifier.weight(1f).combinedClickable(onClick = { onOpen(entry, title, false) }, onLongClick = { menu = entry }, onLongClickLabel = "More options").padding(start = 16.dp, top = 8.dp, bottom = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        Box(Modifier.size(96.dp, 54.dp).clip(MaterialTheme.shapes.small).background(MaterialTheme.colorScheme.surfaceVariant), contentAlignment = Alignment.Center) {
                            Icon(if (entry.kind == RecordingKind.CacheSession) SppIcons.Movie else SppIcons.Library, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                            Text(
                                TimeText.of(entry.durationSeconds),
                                Modifier.align(Alignment.BottomEnd).padding(4.dp).background(Color(0xBF000000), MaterialTheme.shapes.extraSmall).padding(horizontal = 4.dp),
                                color = Color.White,
                                style = MaterialTheme.typography.labelSmall,
                            )
                        }
                        Spacer(Modifier.width(12.dp))
                        Column(Modifier.weight(1f)) {
                            Text(entry.title ?: kindName(entry.kind), style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            val date = DateFormat.getMediumDateFormat(context).format(Date.from(entry.startUtc)) + ", " + DateFormat.getTimeFormat(context).format(Date.from(entry.startUtc))
                            Text(
                                // Steam's own clip titles already are the date and time.
                                listOfNotNull(
                                    date.takeUnless { entry.title?.contains(DATED_TITLE) == true },
                                    entry.sizeBytes.takeIf { it > 0 }?.let { Formatter.formatShortFileSize(context, it) },
                                    entry.markerCount.takeIf { it > 0 }?.let { if (it == 1) "1 event" else "$it events" },
                                ).joinToString(" · "),
                                style = MaterialTheme.typography.bodySmall,
                                color = MaterialTheme.colorScheme.onSurfaceVariant,
                                maxLines = 2,
                                overflow = TextOverflow.Ellipsis,
                            )
                            onPhoneLabel(cached, total)?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.primary) }
                            if (entry.temporary) Text("Steam's rolling cache: it may trim this", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    IconButton({ menu = entry }) { Icon(SppIcons.More, "More options", tint = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
            }
        }
    }

    menu?.let { entry ->
        val cached = here[entry.key]
        val kept = cached?.pinned == true
        val title = entry.title ?: current.game.name
        ModalBottomSheet({ menu = null }, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = MaterialTheme.colorScheme.surfaceContainer) {
            Text(entry.title ?: kindName(entry.kind), Modifier.padding(horizontal = 24.dp, vertical = 8.dp), style = MaterialTheme.typography.titleMedium, maxLines = 1, overflow = TextOverflow.Ellipsis)
            Row(
                Modifier.fillMaxWidth().toggleable(kept, role = Role.Switch) { keep -> vm.setKeptOffline(entry, keep) }.padding(horizontal = 24.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(SppIcons.Pin, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.width(16.dp))
                SppRowText("Keep on this phone", "Downloads the original recording, as it is, to watch without the server. The cache won't remove it. No new video is made.", Modifier.weight(1f).padding(end = 16.dp))
                Switch(kept, null)
            }
            Row(
                Modifier.fillMaxWidth().clickable {
                    menu = null
                    onOpen(entry, title, true)
                }.padding(horizontal = 24.dp, vertical = 12.dp),
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Icon(SppIcons.Export, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                Spacer(Modifier.width(16.dp))
                SppRowText("Export", "Makes a new video file in Movies: choose the quality, and trim it if you want.", Modifier.weight(1f))
            }
            if (cached != null) {
                Row(
                    Modifier.fillMaxWidth().clickable {
                        menu = null
                        vm.removeFromPhone(entry)
                    }.padding(horizontal = 24.dp, vertical = 12.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Icon(SppIcons.Delete, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                    Spacer(Modifier.width(16.dp))
                    SppRowText("Remove from this phone", "${Formatter.formatShortFileSize(context, cached.bytes)} freed. It stays on the server.", Modifier.weight(1f))
                }
            }
            Spacer(Modifier.height(16.dp))
        }
    }
}

private fun onPhoneLabel(cached: app.steampoweredplayer.data.LocalChunkCache.CachedRecording?, total: Int): String? = when {
    cached == null -> null
    cached.pinned -> if (cached.chunks >= total) "Kept on this phone" else "Keeping on this phone: ${cached.chunks * 100 / maxOf(1, total)} %"
    cached.chunks >= total -> "On this phone"
    cached.chunks > 0 -> "${cached.chunks * 100 / maxOf(1, total)} % on this phone"
    else -> null
}

private fun kindName(kind: RecordingKind) = when (kind) {
    RecordingKind.CacheSession -> "Background recording"
    RecordingKind.Clip -> "Saved clip"
    RecordingKind.DownloadedClip -> "Downloaded clip"
}

private val DATED_TITLE = Regex("\\d{4}-\\d{2}-\\d{2}")
private const val STAGGER_MILLIS = 30
private const val FADE_MILLIS = 220
private const val CONNECTING_ALPHA = 0.55f
