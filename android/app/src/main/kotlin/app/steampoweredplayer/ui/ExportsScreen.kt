// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.content.Intent
import android.net.Uri
import android.text.format.DateUtils
import android.text.format.Formatter
import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.PaddingValues
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
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
import app.steampoweredplayer.ui.theme.Brand
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

    LazyColumn(Modifier.fillMaxSize().safeDrawingPadding(), contentPadding = PaddingValues(16.dp), verticalArrangement = Arrangement.spacedBy(10.dp)) {
        item { BrandHeader("Exports", "Saved to Movies/SteamPoweredPlayer") }
        active?.let { a ->
            item {
                SppPanel {
                    Text(a.request.title, style = MaterialTheme.typography.titleSmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
                    LinearProgressIndicator({ a.fraction.toFloat() }, Modifier.fillMaxWidth())
                    Text("${a.stage} · ${(a.fraction * 100).toInt()} %", style = MaterialTheme.typography.bodySmall)
                    TextButton({ Exports.cancelAll(context) }) { Text("Stop") }
                }
            }
        }
        items(queued, key = { "q" + it.id }) { q ->
            SppPanel { Text("${q.title}: waiting", style = MaterialTheme.typography.bodyMedium) }
        }
        if (records.isEmpty() && active == null) {
            item { SppPanel { Text("Nothing exported yet. Open a recording and choose Export.", style = MaterialTheme.typography.bodyMedium) } }
        }
        items(records, key = { it.id }) { r ->
            val tier = if (r.choice == ExportLadder.SAVE_CLIP_ID) "Save clip" else ExportLadder.tier(r.choice)?.name ?: r.choice
            SppPanel(Modifier.clickable(enabled = r.uri != null) { r.uri?.let { open(context, it) } }) {
                Text(r.displayName, style = MaterialTheme.typography.titleSmall, maxLines = 2, overflow = TextOverflow.Ellipsis)
                val age = DateUtils.getRelativeTimeSpanString(r.whenEpochSeconds * 1000)
                if (r.error != null) {
                    Text("$tier · failed: ${r.error}", style = MaterialTheme.typography.bodySmall, color = Brand.Live)
                } else {
                    Text("$tier · ${Formatter.formatShortFileSize(context, r.bytes)} · $age", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
                }
                Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                    r.uri?.let { uri ->
                        TextButton({ open(context, uri) }) { Text("Open") }
                        TextButton({ share(context, uri, r.displayName) }) { Text("Share") }
                    }
                    TextButton({
                        Exports.forget(context, r.id)
                        version++
                    }) { Text("Hide") }
                }
            }
        }
    }
}

private fun open(context: android.content.Context, uri: String) {
    context.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(Uri.parse(uri), "video/mp4").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION))
}

private fun share(context: android.content.Context, uri: String, name: String) {
    val send = Intent(Intent.ACTION_SEND).setType("video/mp4").putExtra(Intent.EXTRA_STREAM, Uri.parse(uri)).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
    context.startActivity(Intent.createChooser(send, name))
}
