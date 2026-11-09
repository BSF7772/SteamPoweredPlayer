// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.text.format.Formatter
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.work.WorkInfo
import androidx.work.WorkManager
import app.steampoweredplayer.AppSettings
import app.steampoweredplayer.data.DownloadStatus
import app.steampoweredplayer.data.LocalChunkCache
import app.steampoweredplayer.net.Downloads
import app.steampoweredplayer.ui.theme.Brand
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** The queue, what's on the phone, the pins and the cache's size. */
@Composable
fun DownloadsScreen(library: LibraryUi) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val progress by Downloads.progress.collectAsState()
    val notice by Downloads.meteredNotice.collectAsState()
    val infos by remember { WorkManager.getInstance(context).getWorkInfosByTagFlow(Downloads.TAG_ALL) }.collectAsState(emptyList())
    var version by remember { mutableIntStateOf(0) }
    var recordings by remember { mutableStateOf<List<LocalChunkCache.CachedRecording>>(emptyList()) }
    var total by remember { mutableStateOf(0L) }
    var cap by remember { mutableIntStateOf(AppSettings(context).cacheGigabytes) }

    LaunchedEffect(version, progress.values.sumOf { it.filesDone }) {
        withContext(Dispatchers.IO) {
            val cache = Downloads.cache(context)
            recordings = cache.recordings()
            total = cache.totalBytes
        }
    }

    val titles = remember(library.games) {
        library.games.flatMap { row -> row.recordings.map { it.relativeFolder.substringAfterLast('/') to (it.title ?: row.game.name) } }.toMap()
    }
    val queued = infos.filter { it.state == WorkInfo.State.ENQUEUED || it.state == WorkInfo.State.RUNNING || it.state == WorkInfo.State.BLOCKED }
        .mapNotNull { info -> info.tags.firstOrNull { it.startsWith("recording:") }?.removePrefix("recording:")?.let { it to info.state } }.distinctBy { it.first }

    LazyColumn(Modifier.fillMaxSize().safeDrawingPadding(), contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item { BrandHeader("Downloads", "${Formatter.formatShortFileSize(context, total)} on this phone of $cap GB") }
        if (notice) item { MeteredNotice() }
        if (queued.isNotEmpty()) item { Text("Queue", style = MaterialTheme.typography.titleSmall) }
        items(queued, key = { "q" + it.first }) { (recording, state) ->
            val p = progress[recording]
            SppPanel {
                Text(titles[recording] ?: recording, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                if (p != null && p.filesTotal > 0) {
                    LinearProgressIndicator({ p.filesDone.toFloat() / p.filesTotal }, Modifier.fillMaxWidth())
                    Text(
                        when (p.status) {
                            DownloadStatus.Reconnecting -> "Reconnecting…"
                            DownloadStatus.CannotReach -> "Can't reach ${library.profile?.name ?: "the server"}: waiting for a network"
                            else -> "${p.filesDone} of ${p.filesTotal} files · ${Formatter.formatShortFileSize(context, p.bytesFromSource)} so far"
                        },
                        style = MaterialTheme.typography.bodySmall,
                        color = if (p.status == DownloadStatus.CannotReach) Brand.Live else MaterialTheme.colorScheme.onSurfaceVariant,
                    )
                } else {
                    Text(if (state == WorkInfo.State.RUNNING) "Starting…" else "Waiting", style = MaterialTheme.typography.bodySmall)
                }
                TextButton({ Downloads.cancel(context, recording) }) { Text("Stop") }
            }
        }
        item { Text("On this phone", style = MaterialTheme.typography.titleSmall) }
        if (recordings.isEmpty()) {
            item { SppPanel { Text("Nothing yet. Opening a recording downloads what it needs.", style = MaterialTheme.typography.bodyMedium) } }
        }
        items(recordings, key = { it.libraryId + "/" + it.recording }) { r ->
            SppPanel {
                Text(titles[r.recording] ?: r.recording, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text("${r.chunks} chunks · ${Formatter.formatShortFileSize(context, r.bytes)}${if (r.pinned) " · kept offline" else ""}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
                    TextButton({
                        scope.launch(Dispatchers.IO) {
                            val cache = Downloads.cache(context)
                            if (r.pinned) cache.unpin(r.libraryId, r.recording) else cache.pin(r.libraryId, r.recording)
                            version++
                        }
                    }) { Text(if (r.pinned) "📌 Unpin" else "📌 Keep offline") }
                    TextButton({
                        scope.launch(Dispatchers.IO) {
                            Downloads.cancel(context, r.recording)
                            Downloads.cache(context).remove(r.libraryId, r.recording)
                            version++
                        }
                    }) { Text("Remove") }
                }
            }
        }
        item {
            SppPanel {
                Text("Cache size", style = MaterialTheme.typography.titleSmall)
                Text("The least recently used recordings make room when it's full. Kept ones stay.", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
                    for (gb in listOf(5, 10, 20, 50)) {
                        FilterChip(cap == gb, {
                            cap = gb
                            scope.launch(Dispatchers.IO) {
                                Downloads.setCacheGigabytes(context, gb)
                                version++
                            }
                        }, { Text("$gb GB") })
                    }
                }
                OutlinedButton({
                    scope.launch(Dispatchers.IO) {
                        Downloads.cache(context).clearUnpinned()
                        version++
                    }
                }) { Text("Clear what isn't kept") }
            }
        }
    }
}
