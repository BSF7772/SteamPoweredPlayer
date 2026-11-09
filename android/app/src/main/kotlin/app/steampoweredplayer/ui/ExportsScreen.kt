// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.content.ClipData
import android.content.Intent
import android.net.Uri
import android.text.format.DateUtils
import android.text.format.Formatter
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import app.steampoweredplayer.export.ExportLadder
import app.steampoweredplayer.export.ExportRecord
import app.steampoweredplayer.export.Exports
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

/** What's being exported and what came out: open it, share it. */
@Composable
fun ExportsScreen() {
    val context = LocalContext.current
    val active by Exports.active.collectAsState()
    val queued by Exports.queued.collectAsState()
    var version by remember { mutableIntStateOf(0) }
    var records by remember { mutableStateOf<List<ExportRecord>>(emptyList()) }
    LaunchedEffect(Unit) { Exports.finished.collect { version++ } }
    LaunchedEffect(version) { records = withContext(Dispatchers.IO) { Exports.history(context) } }

    Column(Modifier.fillMaxSize()) {
        SppTopBar("Exports", "Saved to Movies/SteamPoweredPlayer")
        LazyColumn(Modifier.weight(1f)) {
            if (active != null || queued.isNotEmpty()) item { SppSectionTitle("Exporting") }
            active?.let { a ->
                item {
                    SppRow(Modifier.padding(start = 16.dp, end = 4.dp, top = 8.dp, bottom = 8.dp)) {
                        Column(Modifier.weight(1f), verticalArrangement = Arrangement.spacedBy(6.dp)) {
                            Text(a.request.title, style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                            LinearProgressIndicator({ a.fraction.toFloat() }, Modifier.fillMaxWidth())
                            Text("${a.stage} · ${(a.fraction * 100).toInt()} %", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                        }
                        IconButton({ Exports.cancelAll(context) }) { Icon(SppIcons.Close, "Stop exporting", tint = MaterialTheme.colorScheme.onSurfaceVariant) }
                    }
                }
            }
            items(queued, key = { "q" + it.id }) { q ->
                SppRowText(q.title, "Waiting", Modifier.fillMaxWidth().padding(horizontal = 16.dp, vertical = 8.dp))
            }
            if (records.isEmpty() && active == null) {
                item {
                    Text("Nothing exported yet. Open a recording and press Export.", Modifier.padding(16.dp), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
            }
            if (records.isNotEmpty()) item { SppSectionTitle("Finished") }
            items(records, key = { it.id }) { r ->
                val tier = if (r.choice == ExportLadder.SAVE_CLIP_ID) "Save clip" else ExportLadder.tier(r.choice)?.name ?: r.choice
                val uri = r.uri
                SppRow(Modifier.padding(end = 4.dp)) {
                    SppRow(Modifier.weight(1f).clickable(enabled = uri != null) { uri?.let { open(context, it) } }.padding(start = 16.dp, top = 8.dp, bottom = 8.dp)) {
                        Icon(SppIcons.Movie, null, tint = MaterialTheme.colorScheme.onSurfaceVariant)
                        Spacer(Modifier.width(16.dp))
                        val age = DateUtils.getRelativeTimeSpanString(r.whenEpochSeconds * 1000)
                        SppRowText(
                            r.displayName,
                            if (r.error != null) "$tier · failed: ${r.error}" else "$tier · ${Formatter.formatShortFileSize(context, r.bytes)} · $age",
                            Modifier.weight(1f),
                            detailColor = if (r.error != null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurfaceVariant,
                        )
                    }
                    if (uri != null) IconButton({ share(context, uri, r.displayName) }) { Icon(SppIcons.Share, "Share ${r.displayName}") }
                    IconButton({
                        Exports.forget(context, r.id)
                        version++
                    }) { Icon(SppIcons.Close, "Hide ${r.displayName} from this list", tint = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
            }
            item { Spacer(Modifier.height(16.dp)) }
        }
    }
}

private fun open(context: android.content.Context, uri: String) {
    context.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(Uri.parse(uri), "video/mp4").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
}

private fun share(context: android.content.Context, uri: String, name: String) = shareFile(context, uri, name)

/** The share sheet for a finished export. The clip data gives the sheet the file's name. */
fun shareFile(context: android.content.Context, uri: String, name: String) {
    val stream = Uri.parse(uri)
    val send = Intent(Intent.ACTION_SEND).setType("video/mp4").putExtra(Intent.EXTRA_STREAM, stream).putExtra(Intent.EXTRA_TITLE, name).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    send.clipData = ClipData(name, arrayOf("video/mp4"), ClipData.Item(stream))
    context.startActivity(Intent.createChooser(send, name))
}
