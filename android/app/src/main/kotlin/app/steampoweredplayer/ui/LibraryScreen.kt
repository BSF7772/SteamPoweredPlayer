// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.text.format.DateFormat
import android.text.format.Formatter
import androidx.compose.animation.core.Animatable
import androidx.compose.animation.core.tween
import androidx.compose.foundation.background
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.grid.GridCells
import androidx.compose.foundation.lazy.grid.GridItemSpan
import androidx.compose.foundation.lazy.grid.LazyVerticalGrid
import androidx.compose.foundation.lazy.grid.items
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.produceState
import androidx.compose.runtime.remember
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.alpha
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.layout.ContentScale
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.compose.ui.res.stringResource
import app.steampoweredplayer.Library
import app.steampoweredplayer.R
import app.steampoweredplayer.data.LibrarySource
import app.steampoweredplayer.data.RecordingEntry
import app.steampoweredplayer.data.RecordingKind
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

@Composable
fun LibraryScreen(ui: LibraryUi, vm: LibraryViewModel, onOpenGame: (GameRow) -> Unit, onAddServer: () -> Unit) {
    val context = LocalContext.current
    LazyVerticalGrid(
        columns = GridCells.Adaptive(150.dp),
        modifier = Modifier.fillMaxSize().safeDrawingPadding(),
        contentPadding = PaddingValues(16.dp),
        horizontalArrangement = Arrangement.spacedBy(12.dp),
        verticalArrangement = Arrangement.spacedBy(12.dp),
    ) {
        item(span = { GridItemSpan(maxLineSpan) }) {
            Column(verticalArrangement = Arrangement.spacedBy(8.dp)) {
                BrandHeader("SteamPoweredPlayer", ui.profile?.let { "${it.name} · ${sourceLabel(ui.source)}" }) {
                    if (ui.profile != null) {
                        TextButton({ vm.refresh() }) { Text(if (ui.refreshing) "…" else "Refresh", color = Color.White) }
                    }
                }
                val notice by Downloads.meteredNotice.collectAsState()
                if (notice) MeteredNotice()
                if (ui.source == LibrarySource.Scan) {
                    Text("The desktop app hasn't indexed this share: ids instead of names, and no art.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                ui.message?.let { Text(it, style = MaterialTheme.typography.bodySmall, color = Brand.Live) }
                if (ui.refreshing && ui.games.isEmpty()) LinearProgressIndicator(Modifier.fillMaxWidth())
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
        } else if (ui.games.isEmpty() && !ui.refreshing) {
            item(span = { GridItemSpan(maxLineSpan) }) {
                SppPanel { Text("No recordings here yet.", style = MaterialTheme.typography.bodyMedium) }
            }
        }
        items(ui.games, key = { it.game.gameId + it.game.name }) { row ->
            val index = ui.games.indexOf(row)
            val fade = remember { androidx.compose.animation.core.Animatable(0f) }
            LaunchedEffect(Unit) {
                delay((index.coerceAtMost(12) * STAGGER_MILLIS).toLong())                // the brand's staggered entrance
                fade.animateTo(1f, tween(FADE_MILLIS))
            }
            val art = remember(ui.artVersion, row.game.gameId, ui.profile) { artFor(context, ui, row) }
            Card(
                Modifier.fillMaxWidth().alpha(fade.value).clickable { onOpenGame(row) },
                colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
                shape = MaterialTheme.shapes.large,
            ) {
                Box(Modifier.fillMaxWidth().aspectRatio(0.72f).background(Brand.Gradient), contentAlignment = Alignment.Center) {
                    if (art != null) {
                        AsyncImage(art, row.game.name, Modifier.fillMaxSize(), contentScale = ContentScale.Crop)
                    } else {
                        Text("🎮", style = MaterialTheme.typography.headlineLarge)
                    }
                }
                Column(Modifier.padding(10.dp)) {
                    Text(row.game.name, style = MaterialTheme.typography.titleSmall, fontWeight = FontWeight.SemiBold, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    Text(
                        "${row.recordings.size} · ${TimeText.of(row.totalSeconds)}",
                        style = MaterialTheme.typography.bodySmall,
                        color = MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                }
            }
        }
    }
}

private fun sourceLabel(source: LibrarySource) = when (source) {
    LibrarySource.Index -> "up to date"
    LibrarySource.Scan -> "scanned"
    LibrarySource.Cached -> "offline, as last seen"
    LibrarySource.None -> "not connected"
}

private fun artFor(context: android.content.Context, ui: LibraryUi, row: GameRow): File? {
    val profile = ui.profile ?: return null
    val art = row.game.art ?: return null
    val path = art.cover ?: art.hero ?: art.logo ?: return null
    return Library(context, profile).artFile(row.game.gameId, path).takeIf { it.exists() }
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

@Composable
fun GameScreen(row: GameRow, ui: LibraryUi, vm: LibraryViewModel, onBack: () -> Unit, onOpen: (RecordingEntry, String) -> Unit) {
    val context = LocalContext.current
    val current = ui.games.firstOrNull { it.game.gameId == row.game.gameId } ?: row
    val profile = ui.profile
    val libraryKey = profile?.let { DownloadRoutes.libraryKey(it) }
    val here by produceState<Map<String, Int>>(emptyMap(), libraryKey) {
        value = withContext(Dispatchers.IO) {
            val cache = Downloads.cache(context)
            cache.recordings().filter { it.libraryId == libraryKey }.associate { it.recording to it.chunks }
        }
    }
    LazyColumn(
        Modifier.fillMaxSize().safeDrawingPadding(),
        contentPadding = PaddingValues(16.dp),
        verticalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        item {
            BrandHeader(current.game.name, "${current.recordings.size} recordings · ${TimeText.of(current.totalSeconds)}") {
                TextButton(onBack) { Text("Back", color = Color.White) }
            }
        }
        items(current.recordings, key = { it.relativeFolder }) { entry ->
            val total = entry.runs.sumOf { it.count }
            val chunks = here[entry.key] ?: 0
            SppPanel(Modifier.clickable { onOpen(entry, entry.title ?: current.game.name) }) {
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(if (entry.kind == RecordingKind.CacheSession) "🎥" else "✂️", style = MaterialTheme.typography.titleLarge)
                    Spacer(Modifier.width(12.dp))
                    Column(Modifier.weight(1f)) {
                        Text(entry.title ?: kindName(entry.kind), style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                        val date = DateFormat.getMediumDateFormat(context).format(Date.from(entry.startUtc)) + " " + DateFormat.getTimeFormat(context).format(Date.from(entry.startUtc))
                        Text(
                            buildString {
                                append(date).append(" · ").append(TimeText.of(entry.durationSeconds))
                                if (entry.sizeBytes > 0) append(" · ").append(Formatter.formatShortFileSize(context, entry.sizeBytes))
                                if (entry.markerCount > 0) append(" · ").append(entry.markerCount).append(" events")
                            },
                            style = MaterialTheme.typography.bodySmall,
                            color = MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                        if (entry.temporary) Text("Steam's rolling cache: it may trim this", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                    if (chunks > 0) Text(if (chunks >= total) "📥" else "${chunks * 100 / maxOf(1, total)}%", style = MaterialTheme.typography.bodySmall)
                }
            }
        }
    }
}

private fun kindName(kind: RecordingKind) = when (kind) {
    RecordingKind.CacheSession -> "Background recording"
    RecordingKind.Clip -> "Saved clip"
    RecordingKind.DownloadedClip -> "Downloaded clip"
}

private const val STAGGER_MILLIS = 30
private const val FADE_MILLIS = 220
