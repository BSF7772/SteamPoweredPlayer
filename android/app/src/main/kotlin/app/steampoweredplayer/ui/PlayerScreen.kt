// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.app.Application
import android.graphics.BitmapFactory
import android.text.format.Formatter
import android.view.SurfaceHolder
import android.view.SurfaceView
import android.view.WindowManager
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.detectTapGestures
import androidx.compose.foundation.horizontalScroll
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.FilterChip
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Slider
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.ViewModelStoreOwner
import androidx.lifecycle.viewmodel.compose.viewModel
import app.steampoweredplayer.data.RecordingEntry
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.data.TrickplayTile
import app.steampoweredplayer.data.key
import app.steampoweredplayer.export.CodecMime
import app.steampoweredplayer.export.ExportLadder
import app.steampoweredplayer.export.Exports
import app.steampoweredplayer.export.LadderEngine
import app.steampoweredplayer.export.LadderTier
import app.steampoweredplayer.play.MarkerKind
import app.steampoweredplayer.play.MarkerKinds
import app.steampoweredplayer.play.TimeText
import app.steampoweredplayer.ui.theme.Brand
import java.io.File
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlin.math.abs
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext

private enum class Panel { None, Edit, Sync, Export }

@Composable
fun PlayerScreen(profile: ServerProfile, entry: RecordingEntry, title: String, library: LibraryUi, askForNotifications: () -> Unit, onBack: () -> Unit) {
    // One player per opening of this screen: leaving it closes mpv and stops what it was fetching. Rotation keeps it, as the activity handles that itself.
    val application = LocalContext.current.applicationContext as Application
    val store = remember(entry.relativeFolder) { ViewModelStore() }
    DisposableEffect(store) { onDispose { store.clear() } }
    val owner = remember(store) {
        object : ViewModelStoreOwner {
            override val viewModelStore = store
        }
    }
    val vm: PlayerViewModel = viewModel(owner, factory = ViewModelProvider.AndroidViewModelFactory.getInstance(application))
    val ui by vm.ui.collectAsState()
    LaunchedEffect(entry.relativeFolder) {
        vm.open(profile, entry, library.layout, library.source == app.steampoweredplayer.data.LibrarySource.Scan, title, null)
        askForNotifications()
    }
    val view = LocalView.current
    DisposableEffect(Unit) {
        view.keepScreenOn = true
        onDispose { view.keepScreenOn = false }
    }
    LifecycleEventEffect(Lifecycle.Event.ON_PAUSE) { vm.pauseForBackground() }

    var panel by remember { mutableStateOf(Panel.None) }
    var scrub by remember { mutableStateOf<Double?>(null) }

    val topBar: @Composable () -> Unit = {
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
            TextButton(onBack) { Text("‹ Back", color = Color.White) }
            Text(ui.title, Modifier.weight(1f), color = Color.White, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleSmall)
            TextButton({ vm.togglePin() }) { Text(if (ui.pinned) "📌 Kept" else "📌 Keep", color = Color.White) }
        }
    }
    val panels: @Composable (Modifier) -> Unit = { modifier ->
        ui.error?.let { Text(it, Modifier.padding(8.dp), color = Brand.Live, style = MaterialTheme.typography.bodySmall) }
        Row(Modifier.fillMaxWidth().padding(horizontal = 8.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            PanelTab("✂️ Edit", panel == Panel.Edit) { panel = if (panel == Panel.Edit) Panel.None else Panel.Edit }
            PanelTab("🔊 Sound", panel == Panel.Sync) { panel = if (panel == Panel.Sync) Panel.None else Panel.Sync }
            PanelTab("🎬 Export", panel == Panel.Export) {
                panel = if (panel == Panel.Export) Panel.None else Panel.Export
                vm.loadSourceFormat()
            }
        }
        Column(modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) {
            when (panel) {
                Panel.None -> EventsPanel(vm, ui)
                Panel.Edit -> EditPanel(vm, ui)
                Panel.Sync -> SoundPanel(vm, ui)
                Panel.Export -> ExportPanel(vm, ui, entry, library)
            }
        }
    }
    BoxWithConstraints(Modifier.fillMaxSize().background(Color.Black).safeDrawingPadding()) {
        if (maxWidth > maxHeight) {
            // Sideways: the picture as big as the height allows, everything else scrolling beside it.
            Row(Modifier.fillMaxSize()) {
                Box(Modifier.weight(1f).fillMaxHeight(), contentAlignment = Alignment.Center) {
                    VideoArea(vm, ui, scrub, Modifier.fillMaxHeight().aspectRatio(16f / 9f, matchHeightConstraintsFirst = true)) { scrub = it }
                }
                Column(Modifier.width(SIDE_PANEL_WIDTH).fillMaxHeight().verticalScroll(rememberScrollState())) {
                    topBar()
                    Timeline(vm, ui) { scrub = it }
                    Controls(vm, ui)
                    panels(Modifier)
                }
            }
        } else {
            Column(Modifier.fillMaxSize()) {
                topBar()
                VideoArea(vm, ui, scrub, Modifier.fillMaxWidth().aspectRatio(16f / 9f)) { scrub = it }
                Timeline(vm, ui) { scrub = it }
                Controls(vm, ui)
                panels(Modifier.weight(1f).verticalScroll(rememberScrollState()))
            }
        }
    }
}

@Composable
private fun PanelTab(text: String, selected: Boolean, onClick: () -> Unit) {
    FilterChip(selected, onClick, { Text(text) })
}

@Composable
private fun VideoArea(vm: PlayerViewModel, ui: PlayerUi, scrub: Double?, modifier: Modifier, onScrub: (Double?) -> Unit) {
    var width by remember { mutableStateOf(1) }
    var dragFrom by remember { mutableStateOf(0.0) }
    var doubleTapFlash by remember { mutableStateOf<String?>(null) }
    Box(
        modifier.background(Color.Black)
            .onSizeChanged { width = it.width }
            .pointerInput(Unit) {
                detectTapGestures(
                    onTap = { vm.togglePause() },
                    onDoubleTap = { p ->
                        val right = p.x > size.width / 2
                        vm.seekBy(if (right) 10.0 else -10.0)
                        doubleTapFlash = if (right) "+10 s" else "−10 s"
                    },
                )
            }
            .pointerInput(ui.start, ui.end) {
                // A drag across the picture scrubs: its width is a minute, or the whole recording when that's shorter.
                detectDragGestures(
                    onDragStart = { dragFrom = vm.ui.value.position; onScrub(dragFrom) },
                    onDragEnd = { scrub?.let(vm::seek); onScrub(null) },
                    onDragCancel = { onScrub(null) },
                    onDrag = { change, delta ->
                        change.consume()
                        val span = minOf(60.0, (ui.end - ui.start).coerceAtLeast(1.0))
                        dragFrom = (dragFrom + delta.x / width * span).coerceIn(ui.start, ui.end)
                        onScrub(dragFrom)
                    },
                )
            },
        contentAlignment = Alignment.Center,
    ) {
        AndroidView(
            factory = { context ->
                SurfaceView(context).apply {
                    holder.addCallback(object : SurfaceHolder.Callback {
                        override fun surfaceCreated(h: SurfaceHolder) = vm.surfaceCreated(h.surface)
                        override fun surfaceChanged(h: SurfaceHolder, format: Int, w: Int, height: Int) = vm.surfaceChanged(w, height)
                        override fun surfaceDestroyed(h: SurfaceHolder) = vm.surfaceDestroyed()
                    })
                }
            },
            modifier = Modifier.fillMaxSize(),
        )
        if (ui.loading) {
            Text("Opening…", color = Color.White)
        } else if (ui.waiting) {
            Column(Modifier.background(Color(0xC0101018), MaterialTheme.shapes.medium).padding(16.dp), horizontalAlignment = Alignment.CenterHorizontally) {
                val context = LocalContext.current
                Text(
                    if (ui.waitingOffline) {
                        "Can't reach the server: waiting for a network"
                    } else {
                        // A long recording only fetches what's ahead of the playhead, so its share of the whole can sit at 0 %.
                        listOfNotNull(
                            "Downloading",
                            ui.downloadPercent.takeIf { it > 0 }?.let { "$it %" },
                            ui.downloadBytesPerSecond.takeIf { it > 0 }?.let { "${Formatter.formatShortFileSize(context, it)}/s" },
                        ).joinToString(" · ")
                    },
                    color = Color.White,
                )
                LinearProgressIndicator(Modifier.padding(top = 8.dp))
            }
        } else if (ui.paused && scrub == null) {
            Text(if (ui.atEnd) "↻" else "▶", color = Color.White.copy(alpha = 0.7f), style = MaterialTheme.typography.displayMedium)
        }
        doubleTapFlash?.let { flash ->
            Text(flash, Modifier.align(Alignment.TopCenter).padding(8.dp), color = Color.White, fontWeight = FontWeight.SemiBold)
            LaunchedEffect(flash, ui.position) {
                kotlinx.coroutines.delay(FLASH_MILLIS)
                doubleTapFlash = null
            }
        }
        if (scrub != null) {
            ScrubBubble(vm, scrub, Modifier.align(Alignment.TopCenter).padding(8.dp), ui.start)
        }
    }
}

/** The time being scrubbed to, over its trickplay thumbnail when the desktop made one. */
@Composable
private fun ScrubBubble(vm: PlayerViewModel, seconds: Double, modifier: Modifier, start: Double) {
    val tile = remember(seconds) { vm.tileAt(seconds) }
    Column(modifier.background(Color(0xE0101018), MaterialTheme.shapes.small).padding(4.dp), horizontalAlignment = Alignment.CenterHorizontally) {
        val dir = vm.trickplayDir
        if (tile != null && dir != null) TrickTile(File(dir, tile.sheet), tile)
        Text(TimeText.of(seconds - start), color = Color.White, style = MaterialTheme.typography.labelMedium)
    }
}

private val sheetCache = object : android.util.LruCache<String, ImageBitmap>(4) {}

@Composable
private fun TrickTile(sheet: File, tile: TrickplayTile) {
    var bitmap by remember(sheet.path) { mutableStateOf(sheetCache.get(sheet.path)) }
    LaunchedEffect(sheet.path) {
        if (bitmap == null) {
            bitmap = withContext(Dispatchers.IO) { BitmapFactory.decodeFile(sheet.path)?.asImageBitmap() }
            bitmap?.let { sheetCache.put(sheet.path, it) }
        }
    }
    val image = bitmap
    Canvas(Modifier.size(BUBBLE_WIDTH, BUBBLE_WIDTH * tile.height.toFloat() / tile.width.coerceAtLeast(1))) {
        if (image != null) {
            drawImage(image, IntOffset(tile.x, tile.y), IntSize(tile.width, tile.height), dstSize = IntSize(size.width.toInt(), size.height.toInt()))
        }
    }
}

private val laneColors = mapOf(
    MarkerKind.Kill to Color(0xFF06B6D4), MarkerKind.Death to Color(0xFFEF4444), MarkerKind.MultiKill to Color(0xFFF59E0B), MarkerKind.Objective to Color(0xFF22C55E),
    MarkerKind.Screenshot to Color(0xFFE5E7EB), MarkerKind.Achievement to Color(0xFFFBBF24),
)

@Composable
private fun Timeline(vm: PlayerViewModel, ui: PlayerUi, onScrub: (Double?) -> Unit) {
    var drag by remember { mutableStateOf<Double?>(null) }
    val span = (ui.end - ui.start).coerceAtLeast(0.001)
    Column(Modifier.fillMaxWidth().padding(horizontal = 12.dp, vertical = 4.dp)) {
        BoxWithConstraints(Modifier.fillMaxWidth().height(44.dp)) {
            Canvas(
                Modifier.fillMaxSize()
                    .pointerInput(ui.start, ui.end) {
                        detectTapGestures { p -> vm.seek(ui.start + span * (p.x / size.width).coerceIn(0f, 1f)) }
                    }
                    .pointerInput(ui.start, ui.end) {
                        detectDragGestures(
                            onDragStart = { p ->
                                drag = ui.start + span * (p.x / size.width).coerceIn(0f, 1f)
                                onScrub(drag)
                            },
                            onDragEnd = {
                                drag?.let(vm::seek)
                                drag = null
                                onScrub(null)
                            },
                            onDragCancel = {
                                drag = null
                                onScrub(null)
                            },
                            onDrag = { change, _ ->
                                change.consume()
                                drag = ui.start + span * (change.position.x / size.width).coerceIn(0f, 1f)
                                onScrub(drag)
                            },
                        )
                    },
            ) {
                fun x(t: Double) = ((t - ui.start) / span).toFloat().coerceIn(0f, 1f) * size.width
                val barTop = size.height * 0.45f
                val barHeight = size.height * 0.28f
                drawRect(Color(0xFF2A2A38), Offset(0f, barTop), Size(size.width, barHeight))
                for ((a, b) in ui.downloadedRuns) drawRect(Color(0xFF4B4B60), Offset(x(a), barTop), Size(x(b) - x(a), barHeight))
                val shown = drag ?: ui.position
                drawRect(Brand.GradientH, Offset(0f, barTop), Size(x(shown), barHeight))
                for (k in ui.edit.kept) drawRect(Color(0x8022C55E), Offset(x(k.start), barTop - 3), Size(x(k.end) - x(k.start), barHeight + 6))
                val markIn = ui.edit.markIn
                val markOut = ui.edit.markOut
                if (markIn != null || markOut != null) {
                    val a = markIn ?: ui.start
                    val b = markOut ?: ui.end
                    drawRect(Color(0x80F59E0B), Offset(x(a), barTop - 3), Size(x(b) - x(a), barHeight + 6))
                }
                val loopA = ui.loopA
                if (loopA != null) drawRect(Color(0x60FFFFFF), Offset(x(loopA), barTop - 5), Size((x(ui.loopB ?: ui.position) - x(loopA)).coerceAtLeast(2f), barHeight + 10))
                for (m in ui.markers) {
                    if (m.kind in ui.hiddenKinds) continue
                    drawCircle(laneColors[m.kind] ?: Color.White, 3.5.dp.toPx(), Offset(x(m.marker.startSeconds), barTop - 7.dp.toPx()))
                }
                drawRect(Color.White, Offset(x(shown) - 1.5f, barTop - 6), Size(3f, barHeight + 12))
            }
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween) {
            Text(TimeText.of((drag ?: ui.position) - ui.start), color = Color.White, style = MaterialTheme.typography.labelSmall)
            ui.decoder?.takeIf { it != "no" }?.let { Text(it, color = Color(0x80FFFFFF), style = MaterialTheme.typography.labelSmall) }
            Text(TimeText.of(span), color = Color.White, style = MaterialTheme.typography.labelSmall)
        }
    }
}

@Composable
private fun Controls(vm: PlayerViewModel, ui: PlayerUi) {
    Row(Modifier.fillMaxWidth().padding(vertical = 2.dp), horizontalArrangement = Arrangement.SpaceEvenly, verticalAlignment = Alignment.CenterVertically) {
        ControlButton("⏮") { vm.previousEvent() }
        ControlButton("◁|") { vm.frameBack() }
        ControlButton(if (ui.atEnd) "↻" else if (ui.paused) "▶" else "❚❚", big = true) { vm.togglePause() }
        ControlButton("|▷") { vm.frameForward() }
        ControlButton("⏭") { vm.nextEvent() }
        ControlButton(if (ui.loopA == null) "A" else if (ui.loopB == null) "B" else "A✕B") { vm.loopButton() }
    }
    Row(Modifier.fillMaxWidth().horizontalScroll(rememberScrollState()).padding(horizontal = 8.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        for (s in listOf(0.25, 0.5, 1.0, 1.5, 2.0, 4.0)) {
            FilterChip(abs(ui.edit.speed - s) < 0.001, { vm.setSpeed(s) }, { Text("${if (s % 1.0 == 0.0) s.toInt().toString() else s.toString()}×") })
        }
    }
}

@Composable
private fun ControlButton(label: String, big: Boolean = false, onClick: () -> Unit) {
    Box(
        Modifier.size(if (big) 56.dp else 44.dp).clickable(onClick = onClick),
        contentAlignment = Alignment.Center,
    ) {
        Text(label, color = Color.White, style = if (big) MaterialTheme.typography.titleLarge else MaterialTheme.typography.titleMedium)
    }
}

@Composable
private fun EventsPanel(vm: PlayerViewModel, ui: PlayerUi) {
    if (ui.markers.isEmpty()) {
        Text("No events in this recording.", color = Color(0xFFB9B9C7), style = MaterialTheme.typography.bodySmall)
        return
    }
    val kinds = ui.markers.map { it.kind }.distinct()
    Row(Modifier.horizontalScroll(rememberScrollState()), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        kinds.forEach { k -> FilterChip(k !in ui.hiddenKinds, { vm.toggleKind(k) }, { Text(MarkerKinds.emoji(k) + " " + k.name) }) }
    }
    ui.markers.filter { it.kind !in ui.hiddenKinds }.take(MAX_LISTED_EVENTS).forEach { m ->
        Row(
            Modifier.fillMaxWidth().clickable { vm.seek(app.steampoweredplayer.play.MarkerNavigation.landing(m.marker)) }.padding(vertical = 6.dp),
            horizontalArrangement = Arrangement.spacedBy(10.dp),
        ) {
            Text(MarkerKinds.emoji(m.kind))
            Text(TimeText.of(m.marker.startSeconds - ui.start), color = Color.White, style = MaterialTheme.typography.bodySmall)
            Text(m.marker.entry.title ?: m.kind.name, color = Color(0xFFB9B9C7), style = MaterialTheme.typography.bodySmall, maxLines = 1, overflow = TextOverflow.Ellipsis)
        }
    }
}

@Composable
private fun EditPanel(vm: PlayerViewModel, ui: PlayerUi) {
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton({ vm.markIn() }) { Text("[ In") }
        OutlinedButton({ vm.markOut() }) { Text("Out ]") }
        Button({ vm.addRange() }, enabled = ui.edit.markIn != null || ui.edit.markOut != null) { Text("Keep this") }
    }
    val a = ui.edit.markIn
    val b = ui.edit.markOut
    if (a != null || b != null) {
        Text("Marked: ${a?.let { TimeText.of(it - ui.start) } ?: "start"} to ${b?.let { TimeText.of(it - ui.start) } ?: "end"}", color = Color.White, style = MaterialTheme.typography.bodySmall)
    }
    if (ui.markers.isNotEmpty()) OutlinedButton({ vm.keepHighlights() }) { Text("🔥 Keep around every shown event") }
    if (ui.edit.kept.isEmpty()) {
        Text("Nothing cut: exports take the marks, or the whole recording.", color = Color(0xFFB9B9C7), style = MaterialTheme.typography.bodySmall)
    } else {
        ui.edit.kept.forEach { k ->
            Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                Text("${TimeText.of(k.start - ui.start)} to ${TimeText.of(k.end - ui.start)} (${"%.1f".format(k.end - k.start)} s)", Modifier.weight(1f).clickable { vm.seek(k.start) }, color = Color.White, style = MaterialTheme.typography.bodySmall)
                TextButton({ vm.removeRange(k) }) { Text("Remove") }
            }
        }
        TextButton({ vm.clearEdit() }) { Text("Clear all") }
    }
}

@Composable
private fun SoundPanel(vm: PlayerViewModel, ui: PlayerUi) {
    Text("Volume ${ui.edit.volumePercent.toInt()} %${if (ui.edit.muted) " (muted)" else ""}", color = Color.White, style = MaterialTheme.typography.bodySmall)
    Row(verticalAlignment = Alignment.CenterVertically) {
        Slider(ui.edit.volumePercent.toFloat(), { vm.setVolume(Math.round(it / 5) * 5.0) }, Modifier.weight(1f), valueRange = 0f..130f)
        TextButton({ vm.toggleMute() }) { Text(if (ui.edit.muted) "Unmute" else "Mute") }
    }
    Text("Sound against the picture: ${"%+.0f".format(ui.edit.audioDelay * 1000)} ms", color = Color.White, style = MaterialTheme.typography.bodySmall)
    Slider(ui.edit.audioDelay.toFloat(), { vm.setAudioDelay(Math.round(it * 200) / 200.0) }, valueRange = -1.5f..1.5f)
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp), verticalAlignment = Alignment.CenterVertically) {
        OutlinedButton({ vm.detectSync() }, enabled = !ui.sync.running) { Text("Detect automatically") }
        TextButton({ vm.setAudioDelay(0.0) }) { Text("Reset") }
    }
    if (ui.sync.running) LinearProgressIndicator({ ui.sync.progress.toFloat() }, Modifier.fillMaxWidth())
    ui.sync.message?.let { Text(it, color = Color.White, style = MaterialTheme.typography.bodySmall) }
}

@Composable
private fun ExportPanel(vm: PlayerViewModel, ui: PlayerUi, entry: RecordingEntry, library: LibraryUi) {
    val context = LocalContext.current
    val game = library.games.firstOrNull { it.game.gameId == entry.gameId }?.game?.name
    var targetMb by remember { mutableStateOf("10") }
    val choice = ui.exportChoice
    val stem = remember(entry.key) {
        val date = DateTimeFormatter.ofPattern("yyyy-MM-dd HH.mm.ss").withZone(ZoneId.systemDefault()).format(entry.startUtc)
        sanitize((entry.title?.takeIf { it.isNotBlank() } ?: game ?: "Recording") + if (entry.title.isNullOrBlank()) " $date" else "")
    }
    val active by Exports.active.collectAsState()
    active?.let {
        Text("${it.request.title}: ${it.stage} ${(it.fraction * 100).toInt()} %", color = Color.White, style = MaterialTheme.typography.bodySmall)
        LinearProgressIndicator({ it.fraction.toFloat() }, Modifier.fillMaxWidth())
    }

    TierTile("✂️", "Save clip", "Lossless, the original picture and sound, with events as chapters", choice == ExportLadder.SAVE_CLIP_ID) { vm.setExportChoice(ExportLadder.SAVE_CLIP_ID) }
    ExportLadder.tiers.forEach { TierRow(it, choice == it.id, vm) }
    Text("Special purpose", color = Color(0xFFB9B9C7), style = MaterialTheme.typography.labelMedium)
    ExportLadder.specials.forEach { TierRow(it, choice == it.id, vm) }

    val tier = ExportLadder.tier(choice)
    if (tier != null) {
        Row(horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            LadderEngine.entries.forEach { e ->
                val label = when (e) {
                    LadderEngine.Auto -> "Auto"
                    LadderEngine.Cpu -> "CPU"
                    LadderEngine.Gpu -> "GPU"
                }
                FilterChip(ui.exportEngine == e, { vm.setExportEngine(e) }, { Text(label) })
            }
        }
        if (tier.id == ExportLadder.TARGET_ID) {
            OutlinedTextField(targetMb, { targetMb = it }, label = { Text("Size in MB") }, singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal))
        }
    }
    val target = if (tier?.id == ExportLadder.TARGET_ID) targetMb.toDoubleOrNull() else null
    val seconds = vm.outputSeconds(choice)
    val plan = tier?.let { ExportLadder.plan(it, ui.exportEngine, ui.encoders, ui.sourceWidth, ui.sourceHeight, ui.sourceFps, seconds, target) }
    val summary = when {
        tier == null -> "${"%.1f".format(seconds)} s · about ${Formatter.formatShortFileSize(context, vm.saveClipBytes())} · no re-encode"
        plan == null -> "No encoder on this phone can do this at ${ui.sourceWidth}x${ui.sourceHeight} on this engine."
        else -> "${"%.1f".format(seconds)} s · ${plan.width}x${plan.height} ${codecName(plan.mime)} · about ${Formatter.formatShortFileSize(context, ExportLadder.estimateBytes(plan, seconds))}"
    }
    Text(summary, color = if (tier != null && plan == null) Brand.Live else Color.White, style = MaterialTheme.typography.bodySmall)
    if (tier != null && (ui.edit.speed != 1.0 || ui.edit.muted || ui.edit.volumePercent != 100.0)) {
        Text("Speed, mute and volume apply to this file. Sync applies to every export.", color = Color(0xFFB9B9C7), style = MaterialTheme.typography.bodySmall)
    }
    val ready = tier == null || plan != null
    Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        Button({ vm.export(stem, target, share = false) }, enabled = ready) { Text("Export") }
        OutlinedButton({ vm.export(stem, target, share = true) }, enabled = ready) { Text("Export and share") }
    }
    Text("The file goes to Movies/SteamPoweredPlayer on this phone. Nothing is written to the share.", color = Color(0xFFB9B9C7), style = MaterialTheme.typography.bodySmall)
}

private fun sanitize(name: String) = name.map { if (it in "\\/:*?\"<>|") ' ' else it }.joinToString("").trim().take(MAX_STEM).ifEmpty { "Export" }

private fun codecName(mime: String) = when (mime) {
    CodecMime.AV1 -> "AV1"
    CodecMime.HEVC -> "HEVC"
    else -> "H.264"
}

@Composable
private fun TierRow(tier: LadderTier, selected: Boolean, vm: PlayerViewModel) = TierTile(tier.emoji, tier.name, tier.tagline, selected) { vm.setExportChoice(tier.id) }

@Composable
private fun TierTile(emoji: String, name: String, tagline: String, selected: Boolean, onClick: () -> Unit) {
    val shape = MaterialTheme.shapes.medium
    Row(
        Modifier.fillMaxWidth().clickable(onClick = onClick)
            .background(if (selected) Brand.Purple.copy(alpha = 0.25f) else Color(0xFF1B1B26), shape)
            .then(if (selected) Modifier.border(1.dp, Brand.Cyan, shape) else Modifier)
            .padding(10.dp),
        verticalAlignment = Alignment.CenterVertically,
        horizontalArrangement = Arrangement.spacedBy(10.dp),
    ) {
        Text(emoji, style = MaterialTheme.typography.titleLarge)
        Column {
            Text(name, color = Color.White, style = MaterialTheme.typography.titleSmall)
            Text(tagline, color = Color(0xFFB9B9C7), style = MaterialTheme.typography.bodySmall)
        }
    }
}

private val SIDE_PANEL_WIDTH = 340.dp
private val BUBBLE_WIDTH = 128.dp
private const val MAX_LISTED_EVENTS = 200
private const val MAX_STEM = 120
private const val FLASH_MILLIS = 700L
