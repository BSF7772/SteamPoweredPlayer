// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.text.format.Formatter
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.text.KeyboardActions
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.focus.onFocusChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalFocusManager
import androidx.compose.ui.text.input.ImeAction
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.work.WorkInfo
import androidx.work.WorkManager
import app.steampoweredplayer.AppSettings
import app.steampoweredplayer.data.CacheLimit
import app.steampoweredplayer.data.DownloadStatus
import app.steampoweredplayer.data.LocalChunkCache
import app.steampoweredplayer.net.Downloads
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** The queue, what's on the phone, what's kept and the cache's limit. */
@Composable
fun DownloadsScreen(library: LibraryUi) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    val focus = LocalFocusManager.current
    val progress by Downloads.progress.collectAsState()
    val notice by Downloads.meteredNotice.collectAsState()
    val infos by remember { WorkManager.getInstance(context).getWorkInfosByTagFlow(Downloads.TAG_ALL) }.collectAsState(emptyList())
    var version by remember { mutableIntStateOf(0) }
    var recordings by remember { mutableStateOf<List<LocalChunkCache.CachedRecording>>(emptyList()) }
    var total by remember { mutableStateOf(0L) }
    var cap by remember { mutableIntStateOf(AppSettings(context).cacheGigabytes) }
    var capText by remember { mutableStateOf(cap.toString()) }

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

    // Applied when typing is done, not on each digit: "1" on the way to "10" would throw recordings away. Anything that isn't a limit goes back to the one in force.
    val commitLimit = {
        val typed = CacheLimit.parse(capText)
        if (typed == null) {
            capText = cap.toString()
        } else if (typed != cap) {
            cap = typed
            capText = typed.toString()
            scope.launch(Dispatchers.IO) {
                Downloads.setCacheGigabytes(context, typed)
                version++
            }
        }
    }

    Column(Modifier.fillMaxSize()) {
        SppTopBar("Downloads", "${Formatter.formatShortFileSize(context, total)} of $cap GB on this phone")
        LazyColumn(Modifier.weight(1f).imePadding()) {
            if (notice) item { Column(Modifier.padding(horizontal = 16.dp)) { MeteredNotice() } }
            if (queued.isNotEmpty()) item { SppSectionTitle("Downloading") }
            items(queued, key = { "q" + it.first }) { (recording, state) ->
                val p = progress[recording]
                SppRow(Modifier.padding(start = 16.dp, end = 4.dp, top = 8.dp, bottom = 8.dp)) {
                    Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                        Text(titles[recording] ?: recording, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                        if (p != null && p.filesTotal > 0) {
                            LinearProgressIndicator({ p.filesDone.toFloat() / p.filesTotal }, Modifier.fillMaxWidth())
                            Text(
                                when (p.status) {
                                    DownloadStatus.Reconnecting -> "Reconnecting…"
                                    DownloadStatus.CannotReach -> "Can't reach ${library.profile?.name ?: "the server"}: waiting for a network"
                                    else -> "${p.filesDone} of ${p.filesTotal} files · ${Formatter.formatShortFileSize(context, p.bytesFromSource)} so far"
                                },
                                style = MaterialTheme.typography.bodySmall,
                                color = if (p.status == DownloadStatus.CannotReach) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                            )
                        } else {
                            Text(if (state == WorkInfo.State.RUNNING) "Starting…" else "Waiting", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                    }
                    IconButton({ Downloads.cancel(context, recording) }) { Icon(SppIcons.Close, "Stop download", tint = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
            }
            item { SppSectionTitle("On this phone") }
            if (recordings.isEmpty()) {
                item {
                    Text("Nothing yet. Opening a recording downloads what it needs.", Modifier.padding(horizontal = 16.dp, vertical = 8.dp), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            items(recordings, key = { it.libraryId + "/" + it.recording }) { r ->
                SppRow(Modifier.padding(start = 16.dp, end = 4.dp, top = 8.dp, bottom = 8.dp)) {
                    SppRowText(
                        titles[r.recording] ?: r.recording,
                        "${r.chunks} chunks · ${Formatter.formatShortFileSize(context, r.bytes)}${if (r.pinned) " · Kept on this phone" else ""}",
                        Modifier.weight(1f),
                    )
                    IconButton({
                        scope.launch(Dispatchers.IO) {
                            val cache = Downloads.cache(context)
                            if (r.pinned) cache.unpin(r.libraryId, r.recording) else cache.pin(r.libraryId, r.recording)
                            version++
                        }
                    }) { Icon(SppIcons.Pin, if (r.pinned) "Stop keeping on this phone" else "Keep on this phone", tint = if (r.pinned) MaterialTheme.colorScheme.primary else MaterialTheme.colorScheme.onSurfaceVariant) }
                    IconButton({
                        scope.launch(Dispatchers.IO) {
                            Downloads.cancel(context, r.recording)
                            Downloads.cache(context).remove(r.libraryId, r.recording)
                            version++
                        }
                    }) { Icon(SppIcons.Delete, "Remove from this phone", tint = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
            }
            item {
                SppSectionTitle("Storage")
                Column(Modifier.padding(horizontal = 16.dp, vertical = 8.dp)) {
                    OutlinedTextField(
                        capText,
                        { capText = it.filter(Char::isDigit) },
                        Modifier.fillMaxWidth().onFocusChanged { if (!it.isFocused) commitLimit() },
                        singleLine = true,
                        label = { Text("Playback cache limit") },
                        suffix = { Text("GB") },
                        isError = capText.isNotEmpty() && CacheLimit.parse(capText) == null,
                        supportingText = { Text("The least recently watched recordings make room when it's full. Kept ones stay.") },
                        keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Number, imeAction = ImeAction.Done),
                        keyboardActions = KeyboardActions(onDone = {
                            commitLimit()
                            focus.clearFocus()
                        }),
                    )
                    Spacer(Modifier.height(8.dp))
                    OutlinedButton({
                        scope.launch(Dispatchers.IO) {
                            Downloads.cache(context).clearUnpinned()
                            version++
                        }
                    }) { Text("Clear what isn't kept") }
                }
                Spacer(Modifier.height(16.dp))
            }
        }
    }
}
