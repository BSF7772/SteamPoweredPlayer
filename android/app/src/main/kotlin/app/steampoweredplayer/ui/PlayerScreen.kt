// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.app.Activity
import android.app.Application
import android.content.Context
import android.content.ContextWrapper
import android.content.pm.ActivityInfo
import android.graphics.BitmapFactory
import android.content.Intent
import android.os.SystemClock
import android.provider.Settings
import android.text.format.Formatter
import android.view.OrientationEventListener
import android.view.SurfaceHolder
import android.view.SurfaceView
import androidx.activity.compose.BackHandler
import androidx.compose.foundation.Canvas
import androidx.compose.foundation.background
import androidx.compose.foundation.border
import androidx.compose.foundation.clickable
import androidx.compose.foundation.gestures.awaitEachGesture
import androidx.compose.foundation.gestures.awaitFirstDown
import androidx.compose.foundation.gestures.detectDragGestures
import androidx.compose.foundation.gestures.waitForUpOrCancellation
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.BoxScope
import androidx.compose.foundation.layout.BoxWithConstraints
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.FlowRow
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.aspectRatio
import androidx.compose.foundation.layout.fillMaxHeight
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.offset
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawing
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.statusBarsPadding
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.layout.windowInsetsPadding
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.foundation.relocation.BringIntoViewRequester
import androidx.compose.foundation.relocation.bringIntoViewRequester
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.shape.CircleShape
import androidx.compose.foundation.shape.RoundedCornerShape
import androidx.compose.foundation.systemGestureExclusion
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.ButtonDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.ExperimentalMaterial3Api
import androidx.compose.material3.FilterChip
import androidx.compose.material3.Icon
import androidx.compose.material3.IconButton
import androidx.compose.material3.LinearProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.ModalBottomSheet
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.SegmentedButton
import androidx.compose.material3.SegmentedButtonDefaults
import androidx.compose.material3.SingleChoiceSegmentedButtonRow
import androidx.compose.material3.Slider
import androidx.compose.material3.SnackbarDuration
import androidx.compose.material3.SnackbarHost
import androidx.compose.material3.SnackbarHostState
import androidx.compose.material3.SnackbarResult
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.material3.rememberModalBottomSheetState
import androidx.compose.runtime.Composable
import androidx.compose.runtime.DisposableEffect
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.key
import androidx.compose.runtime.mutableIntStateOf
import androidx.compose.runtime.mutableLongStateOf
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.rememberCoroutineScope
import androidx.compose.runtime.rememberUpdatedState
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.draw.clip
import androidx.compose.ui.geometry.Offset
import androidx.compose.ui.geometry.Size
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.graphics.ImageBitmap
import androidx.compose.ui.graphics.asImageBitmap
import androidx.compose.ui.graphics.drawscope.Stroke
import androidx.compose.ui.graphics.vector.ImageVector
import androidx.compose.ui.input.pointer.pointerInput
import androidx.compose.ui.layout.onSizeChanged
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalDensity
import androidx.compose.ui.platform.LocalView
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.semantics.contentDescription
import androidx.compose.ui.semantics.onClick
import androidx.compose.ui.semantics.role
import androidx.compose.ui.semantics.semantics
import androidx.compose.ui.text.TextStyle
import androidx.compose.ui.text.drawText
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.rememberTextMeasurer
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.IntOffset
import androidx.compose.ui.unit.IntSize
import androidx.compose.ui.unit.dp
import androidx.compose.ui.unit.sp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.WindowInsetsControllerCompat
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.ViewModelProvider
import androidx.lifecycle.ViewModelStore
import androidx.lifecycle.ViewModelStoreOwner
import androidx.lifecycle.compose.LifecycleEventEffect
import androidx.lifecycle.viewmodel.compose.viewModel
import app.steampoweredplayer.data.RecordingEntry
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.data.TrickplayTile
import app.steampoweredplayer.data.key
import app.steampoweredplayer.export.CodecMime
import app.steampoweredplayer.export.ExportLadder
import app.steampoweredplayer.export.ExportScope
import app.steampoweredplayer.export.ExportScopes
import app.steampoweredplayer.export.Exports
import app.steampoweredplayer.export.LadderEngine
import app.steampoweredplayer.play.LoopHandles
import app.steampoweredplayer.play.MarkerKind
import app.steampoweredplayer.play.MarkerKinds
import app.steampoweredplayer.play.MarkerNavigation
import app.steampoweredplayer.play.TimeText
import app.steampoweredplayer.play.TimelineGrab
import app.steampoweredplayer.ui.theme.Brand
import java.io.File
import java.time.ZoneId
import java.time.format.DateTimeFormatter
import kotlin.math.abs
import kotlin.math.roundToInt
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

private enum class Sheet { Speed, Sync, Volume, Events, Loop, Export }

/** What the timeline's finger is doing, for the bubble over the picture. */
private data class Scrub(val seconds: Double, val label: String?)

@OptIn(ExperimentalMaterial3Api::class)
@Composable
fun PlayerScreen(
    profile: ServerProfile,
    entry: RecordingEntry,
    title: String,
    library: LibraryUi,
    openExport: Boolean,
    askForNotifications: () -> Unit,
    onBack: () -> Unit,
    onViewExports: () -> Unit,
) {
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
    LaunchedEffect(ui.loading) { if (!ui.loading) vm.loadSourceFormat() }                // the ladder's size estimates need the source's real picture size

    var sheet by remember { mutableStateOf(if (openExport) Sheet.Export else null) }
    var scrub by remember { mutableStateOf<Scrub?>(null) }
    var controls by remember { mutableStateOf(true) }
    var touched by remember { mutableLongStateOf(0L) }
    val snackbars = remember { SnackbarHostState() }
    val scope = rememberCoroutineScope()
    val orientation = rememberOrientationControl()
    val keepControls: () -> Unit = { touched++ }
    val openSheet: (Sheet) -> Unit = { s ->
        sheet = s
        keepControls()
    }
    val context = LocalContext.current
    val takeScreenshot: () -> Unit = {
        keepControls()
        scope.launch {
            try {
                // Named after the game, like the desktop; a recording the library doesn't list falls back to its own title.
                val game = library.games.firstOrNull { row -> row.recordings.any { it.relativeFolder == entry.relativeFolder } }?.game?.name
                val (name, uri) = vm.screenshot(game ?: ui.title)
                // The toast names the file; its action opens it.
                val result = snackbars.showSnackbar("Screenshot saved: $name", "Open", duration = SnackbarDuration.Short)
                if (result == SnackbarResult.ActionPerformed) {
                    runCatching {
                        context.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, "image/png").addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK))
                    }
                }
            } catch (e: java.io.IOException) {
                snackbars.showSnackbar("Couldn't save the screenshot: ${e.message}", duration = SnackbarDuration.Short)
            }
        }
    }

    // The overlay goes away by itself while playing; a paused picture keeps its controls until a tap.
    LaunchedEffect(controls, ui.paused, touched, sheet, scrub != null) {
        if (controls && !ui.paused && !ui.waiting && sheet == null && scrub == null) {
            delay(CONTROLS_MILLIS)
            controls = false
        }
    }

    BoxWithConstraints(Modifier.fillMaxSize().background(Color.Black)) {
        val landscape = maxWidth > maxHeight
        ImmersiveWhile(landscape)
        BackHandler(enabled = landscape) { orientation.exitFullscreen() }

        // One tree for both orientations, so the video surface and the player survive a rotation.
        Column(Modifier.fillMaxSize().then(if (landscape) Modifier else Modifier.windowInsetsPadding(WindowInsets.safeDrawing))) {
            if (!landscape) PortraitTopBar(ui.title, onBack, takeScreenshot) { openSheet(Sheet.Export) }
            Box(Modifier.fillMaxWidth().then(if (landscape) Modifier.fillMaxHeight() else Modifier)) {
                Column {
                    VideoArea(
                        vm, ui, scrub, controls, landscape,
                        if (landscape) Modifier.fillMaxSize() else Modifier.fillMaxWidth().aspectRatio(16f / 9f),
                        onShowControls = {
                            controls = !controls
                            keepControls()
                        },
                        onDoubleTap = {
                            controls = true
                            keepControls()
                        },
                        onKeepControls = keepControls,
                        onScrub = { scrub = it },
                        onFullscreen = { orientation.enterFullscreen() },
                    ) {
                        if (landscape && controls) {
                            FullscreenOverlay(ui, vm, keepControls, onExit = { orientation.exitFullscreen() }, onBack = { orientation.exitFullscreen() }, onSheet = openSheet, onScreenshot = takeScreenshot, onScrub = { scrub = it })
                        }
                    }
                    if (!landscape) Spacer(Modifier.height(LANE_HEIGHT))
                }
                if (!landscape || controls) {
                    Timeline(vm, ui, if (landscape) FULLSCREEN_LINE_INSET else 0.dp, Modifier.align(Alignment.BottomStart).then(if (landscape) Modifier.padding(bottom = 4.dp) else Modifier), keepControls) { scrub = it }
                }
            }
            if (!landscape) {
                TimeRow(ui) { openSheet(Sheet.Events) }
                ui.error?.let { Text(it, Modifier.padding(horizontal = 16.dp), color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodySmall) }
                TransportRow(vm, ui, keepControls) { scrub = it }
                Box(Modifier.padding(horizontal = 16.dp, vertical = 4.dp)) { OptionsRow(vm, ui, openSheet) }
                Spacer(Modifier.height(8.dp))
                Ladder(vm, ui, Modifier.padding(horizontal = 16.dp))
            }
        }
        SnackbarHost(snackbars, Modifier.align(Alignment.BottomCenter).navigationBarsPadding())
    }

    sheet?.let { current ->
        val close = { sheet = null }
        ModalBottomSheet(close, sheetState = rememberModalBottomSheetState(skipPartiallyExpanded = true), containerColor = MaterialTheme.colorScheme.surfaceContainer) {
            Column(Modifier.imePadding().verticalScroll(rememberScrollState()).padding(horizontal = 24.dp).padding(bottom = 16.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                when (current) {
                    Sheet.Speed -> SpeedSheet(vm, ui, close)
                    Sheet.Sync -> SyncSheet(vm, ui)
                    Sheet.Volume -> VolumeSheet(vm, ui)
                    Sheet.Events -> EventsSheet(vm, ui, close)
                    Sheet.Loop -> LoopSheet(vm, ui)
                    Sheet.Export -> ExportSheet(vm, ui, entry, library) { share ->
                        close()
                        scope.launch {
                            val result = snackbars.showSnackbar(if (share) "Exporting, then sharing" else "Exporting", "View", duration = SnackbarDuration.Short)
                            if (result == SnackbarResult.ActionPerformed) onViewExports()
                        }
                    }
                }
            }
        }
    }
}

@Composable
private fun PortraitTopBar(title: String, onBack: () -> Unit, onScreenshot: () -> Unit, onExport: () -> Unit) {
    Row(Modifier.fillMaxWidth().height(56.dp).padding(start = 4.dp, end = 12.dp), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onBack) { Icon(SppIcons.Back, "Back", tint = Color.White) }
        Text(title, Modifier.weight(1f).padding(horizontal = 4.dp), color = Color.White, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium)
        IconButton(onScreenshot) { Icon(SppIcons.Screenshot, "Screenshot", tint = Color.White) }
        ExportButton(onExport)
    }
}

/** The one export action of the player, always in the top right corner. */
@Composable
private fun ExportButton(onClick: () -> Unit) {
    Button(onClick, contentPadding = ButtonDefaults.ButtonWithIconContentPadding) {
        Icon(SppIcons.Export, null, Modifier.size(20.dp))
        Spacer(Modifier.width(8.dp))
        Text("Export")
    }
}

/** The system bars go while sideways: the video has the whole screen. A swipe brings them back for a moment. */
@Composable
private fun ImmersiveWhile(active: Boolean) {
    val view = LocalView.current
    DisposableEffect(active) {
        val window = view.context.findActivity()?.window
        val bars = window?.let { WindowCompat.getInsetsController(it, view) }
        bars?.systemBarsBehavior = WindowInsetsControllerCompat.BEHAVIOR_SHOW_TRANSIENT_BARS_BY_SWIPE
        if (active) bars?.hide(WindowInsetsCompat.Type.systemBars()) else bars?.show(WindowInsetsCompat.Type.systemBars())
        onDispose { bars?.show(WindowInsetsCompat.Type.systemBars()) }
    }
}

private tailrec fun Context.findActivity(): Activity? = when (this) {
    is Activity -> this
    is ContextWrapper -> baseContext.findActivity()
    else -> null
}

/**
 * The fullscreen button forces an orientation, then gives control back to the sensor once the phone is really held that way: otherwise turning the phone would stop working, or
 * the screen would snap straight back. With auto-rotate off the forced orientation just stays until the button is pressed again.
 */
private class OrientationControl(private val activity: Activity?) {
    private var listener: OrientationEventListener? = null

    fun enterFullscreen() = force(ActivityInfo.SCREEN_ORIENTATION_SENSOR_LANDSCAPE) { it in 60..120 || it in 240..300 }

    fun exitFullscreen() = force(ActivityInfo.SCREEN_ORIENTATION_PORTRAIT) { it <= 30 || it >= 330 }

    private fun force(orientation: Int, heldThatWay: (Int) -> Boolean) {
        val a = activity ?: return
        listener?.disable()
        a.requestedOrientation = orientation
        val autoRotate = Settings.System.getInt(a.contentResolver, Settings.System.ACCELEROMETER_ROTATION, 0) == 1
        if (!autoRotate) return
        listener = object : OrientationEventListener(a) {
            override fun onOrientationChanged(degrees: Int) {
                if (degrees != ORIENTATION_UNKNOWN && heldThatWay(degrees)) {
                    a.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
                    disable()
                }
            }
        }.also { it.enable() }
    }

    fun release() {
        listener?.disable()
        listener = null
    }
}

@Composable
private fun rememberOrientationControl(): OrientationControl {
    val activity = LocalContext.current.findActivity()
    val control = remember(activity) { OrientationControl(activity) }
    DisposableEffect(control) {
        onDispose {
            control.release()
            activity?.requestedOrientation = ActivityInfo.SCREEN_ORIENTATION_UNSPECIFIED
        }
    }
    return control
}

/**
 * The picture and its gestures. A tap shows or hides the controls at once, with a faint pause button in the middle, which pauses as soon as it's pressed. Double-tapping the left or
 * right half seeks 5 s and further taps add to it. A drag across the picture scrubs.
 */
@Composable
private fun VideoArea(
    vm: PlayerViewModel,
    ui: PlayerUi,
    scrub: Scrub?,
    controls: Boolean,
    landscape: Boolean,
    modifier: Modifier,
    onShowControls: () -> Unit,
    onDoubleTap: () -> Unit,
    onKeepControls: () -> Unit,
    onScrub: (Scrub?) -> Unit,
    onFullscreen: () -> Unit,
    overlay: @Composable BoxScope.() -> Unit,
) {
    var width by remember { mutableIntStateOf(1) }
    var dragFrom by remember { mutableStateOf(0.0) }
    var seekFlash by remember { mutableStateOf(0) }
    var seekSerial by remember { mutableIntStateOf(0) }
    var flashLabel by remember { mutableStateOf<String?>(null) }
    val latestShow by rememberUpdatedState(onShowControls)
    val latestDouble by rememberUpdatedState(onDoubleTap)
    Box(
        modifier.background(Color.Black)
            .onSizeChanged { width = it.width }
            .pointerInput(Unit) {
                var lastUp = 0L
                var lastSide = 0
                awaitEachGesture {
                    awaitFirstDown()
                    val up = waitForUpOrCancellation() ?: return@awaitEachGesture
                    val side = if (up.position.x > size.width / 2) 1 else -1
                    val quick = lastUp != 0L && up.uptimeMillis - lastUp <= viewConfiguration.doubleTapTimeoutMillis && side == lastSide
                    lastUp = up.uptimeMillis
                    lastSide = side
                    if (quick) {
                        // With a handle selected, every step control moves the handle: a double tap jumps it a chunk.
                        val handle = vm.ui.value.selectedHandle
                        val step = if (handle != null) LoopHandles.JUMP_SECONDS.toInt() else DOUBLE_TAP_SECONDS.toInt()
                        if (handle != null) vm.jumpHandle(side) else vm.seekBy(side * DOUBLE_TAP_SECONDS)
                        flashLabel = handle?.name
                        seekFlash = if (seekFlash.sign() == side) seekFlash + side * step else side * step
                        seekSerial++
                        latestDouble()
                    } else {
                        latestShow()                                                  // at once: nothing here waits to see whether a second tap comes
                    }
                }
            }
            .pointerInput(ui.start, ui.end) {
                // A drag across the picture scrubs: its width is a minute, or the whole recording when that's shorter.
                detectDragGestures(
                    onDragStart = {
                        dragFrom = vm.ui.value.position
                        onScrub(Scrub(dragFrom, null))
                    },
                    onDragEnd = {
                        vm.seek(dragFrom)
                        onScrub(null)
                    },
                    onDragCancel = { onScrub(null) },
                    onDrag = { change, delta ->
                        change.consume()
                        val span = minOf(60.0, (ui.end - ui.start).coerceAtLeast(1.0))
                        dragFrom = (dragFrom + delta.x / width * span).coerceIn(ui.start, ui.end)
                        onScrub(Scrub(dragFrom, null))
                    },
                )
            },
        contentAlignment = Alignment.Center,
    ) {
        // A new surface for each orientation: mpv doesn't redraw a paused picture on a resize, and would show it at the old geometry until the next frame.
        key(landscape) {
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
        }
        overlay()
        if (ui.loading) {
            CircularProgressIndicator(color = Color.White)
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
        } else if (controls && !landscape && scrub == null) {
            CentrePlayPause(vm, ui, onKeepControls)
        }
        if (controls && !landscape) {
            // Clear of the timeline's touch area, which reaches up over the picture.
            IconButton(onFullscreen, Modifier.align(Alignment.BottomEnd).padding(bottom = TIMELINE_TOUCH_HEIGHT - LANE_HEIGHT)) { Icon(SppIcons.Fullscreen, "Fullscreen", tint = Color.White) }
        }
        if (seekFlash != 0) {
            SeekFlash(seekFlash, flashLabel, Modifier.align(if (seekFlash > 0) Alignment.CenterEnd else Alignment.CenterStart))
            LaunchedEffect(seekSerial) {
                delay(FLASH_MILLIS)
                seekFlash = 0
            }
        }
        if (scrub != null) {
            ScrubBubble(vm, scrub, ui.start, width, Modifier.align(Alignment.BottomStart).padding(bottom = 30.dp))
        }
    }
}

private fun Int.sign() = if (this > 0) 1 else if (this < 0) -1 else 0

/** The faint pause button that a tap on the picture brings up. It stays out of the way of a fast second tap, which belongs to the double-tap seek. */
@Composable
private fun CentrePlayPause(vm: PlayerViewModel, ui: PlayerUi, onKeepControls: () -> Unit) {
    var armed by remember { mutableStateOf(false) }
    LaunchedEffect(Unit) {
        delay(DOUBLE_TAP_GUARD_MILLIS)
        armed = true
    }
    Box(
        Modifier.size(64.dp).clip(CircleShape).background(Color(0x59000000))
            .then(
                if (armed) {
                    Modifier.clickable(role = Role.Button) {
                        vm.togglePause()
                        onKeepControls()
                    }
                } else {
                    Modifier
                },
            ),
        contentAlignment = Alignment.Center,
    ) {
        Icon(playIcon(ui), playLabel(ui), Modifier.size(36.dp), tint = Color.White.copy(alpha = if (ui.paused) 0.95f else 0.7f))
    }
}

private fun playIcon(ui: PlayerUi): ImageVector = when {
    ui.atEnd -> SppIcons.Replay
    ui.paused -> SppIcons.Play
    else -> SppIcons.Pause
}

private fun playLabel(ui: PlayerUi) = when {
    ui.atEnd -> "Play again"
    ui.paused -> "Play"
    else -> "Pause"
}

/** The half-moon over the side that was double-tapped. */
@Composable
private fun SeekFlash(seconds: Int, handle: String?, modifier: Modifier) {
    val forward = seconds > 0
    val shape = if (forward) RoundedCornerShape(topStartPercent = 50, bottomStartPercent = 50) else RoundedCornerShape(topEndPercent = 50, bottomEndPercent = 50)
    Box(modifier.fillMaxHeight().fillMaxWidth(SEEK_FLASH_WIDTH).background(Color.White.copy(alpha = 0.14f), shape), contentAlignment = Alignment.Center) {
        Text(listOfNotNull(handle, (if (forward) "+" else "−") + "${abs(seconds)} s").joinToString(" "), color = Color.White, fontWeight = FontWeight.Medium)
    }
}

/** The time being scrubbed to, over its trickplay thumbnail when the desktop made one, following the finger. */
@Composable
private fun ScrubBubble(vm: PlayerViewModel, scrub: Scrub, start: Double, areaWidth: Int, modifier: Modifier) {
    val tile = remember(scrub.seconds) { vm.tileAt(scrub.seconds) }
    var bubbleWidth by remember { mutableIntStateOf(0) }
    val span = (vm.ui.value.end - start).coerceAtLeast(0.001)
    val x = (((scrub.seconds - start) / span) * areaWidth).toInt() - bubbleWidth / 2
    Column(
        modifier.offset { IntOffset(x.coerceIn(0, (areaWidth - bubbleWidth).coerceAtLeast(0)), 0) }
            .onSizeChanged { bubbleWidth = it.width }
            .background(Color(0xE0101018), MaterialTheme.shapes.small).padding(4.dp),
        horizontalAlignment = Alignment.CenterHorizontally,
    ) {
        val dir = vm.trickplayDir
        if (tile != null && dir != null) TrickTile(File(dir, tile.sheet), tile)
        Text(listOfNotNull(scrub.label, TimeText.of(scrub.seconds - start)).joinToString(" "), color = Color.White, style = MaterialTheme.typography.labelMedium)
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
    MarkerKind.Kill to Color(0xFF22D3EE), MarkerKind.Death to Color(0xFFF87171), MarkerKind.MultiKill to Color(0xFFFBBF24), MarkerKind.Objective to Color(0xFF4ADE80),
    MarkerKind.Screenshot to Color(0xFFE5E7EB), MarkerKind.Achievement to Color(0xFFFDE68A),
)

private val LoopColor = Color(0xFFF59E0B)
private val CutColor = Color(0xFF4ADE80)

/**
 * The thin line on the video's bottom edge, with the markers right below it. It's drawn thin and touched big: the touch area reaches over the picture and over the markers. A press
 * near an A or B handle moves that handle; anywhere else scrubs.
 */
@Composable
private fun Timeline(vm: PlayerViewModel, ui: PlayerUi, inset: androidx.compose.ui.unit.Dp, modifier: Modifier, onKeepControls: () -> Unit, onScrub: (Scrub?) -> Unit) {
    var scrubbing by remember { mutableStateOf<Double?>(null) }
    val latest by rememberUpdatedState(ui)
    val measurer = rememberTextMeasurer()
    val density = LocalDensity.current
    val labelStyle = remember { TextStyle(color = Color(0xFF2A1600), fontSize = 13.sp, fontWeight = FontWeight.Bold) }
    val a = remember(measurer) { measurer.measure("A", labelStyle) }
    val b = remember(measurer) { measurer.measure("B", labelStyle) }
    val insetPx = with(density) { inset.toPx() }
    val reachPx = with(density) { HANDLE_REACH.toPx() }
    Canvas(
        modifier.fillMaxWidth().height(TIMELINE_TOUCH_HEIGHT)
            .systemGestureExclusion()
            .semantics { contentDescription = "Timeline" }
            .pointerInput(insetPx) {
                awaitEachGesture {
                    val down = awaitFirstDown()
                    down.consume()
                    val line = (size.width - 2 * insetPx).coerceAtLeast(1f)
                    fun secondsAt(x: Float): Double {
                        val u = latest
                        return u.start + (u.end - u.start).coerceAtLeast(0.001) * ((x - insetPx) / line).coerceIn(0f, 1f)
                    }
                    fun xOf(seconds: Double?): Float? {
                        val u = latest
                        return seconds?.let { insetPx + ((it - u.start) / (u.end - u.start).coerceAtLeast(0.001)).toFloat() * line }
                    }
                    val grab = LoopHandles.grab(down.position.x, xOf(latest.loopA), xOf(latest.loopB), reachPx)
                    val slop = viewConfiguration.touchSlop
                    var moved = false
                    fun follow(x: Float) {
                        val t = secondsAt(x)
                        when (grab) {
                            TimelineGrab.A -> {
                                vm.dragLoopA(t)
                                onScrub(Scrub(vm.ui.value.loopA ?: t, "A"))
                            }
                            TimelineGrab.B -> {
                                vm.dragLoopB(t)
                                onScrub(Scrub(vm.ui.value.loopB ?: t, "B"))
                            }
                            TimelineGrab.Seek -> {
                                scrubbing = t
                                onScrub(Scrub(t, null))
                            }
                        }
                    }
                    // A handle stays put until the finger really moves, so a tap only selects it.
                    if (grab == TimelineGrab.Seek) follow(down.position.x)
                    onKeepControls()
                    var lastX = down.position.x
                    var lifted = false
                    while (true) {
                        val event = awaitPointerEvent()
                        val change = event.changes.firstOrNull { it.id == down.id } ?: break
                        if (!change.pressed) {
                            lifted = true
                            change.consume()
                            break
                        }
                        change.consume()
                        if (!moved && abs(change.position.x - down.position.x) > slop) moved = true
                        if (change.position.x != lastX && (moved || grab == TimelineGrab.Seek)) {
                            lastX = change.position.x
                            follow(lastX)
                        }
                    }
                    when {
                        grab == TimelineGrab.Seek -> {
                            vm.releaseHandle()
                            if (lifted) scrubbing?.let(vm::seek)
                        }
                        moved -> {
                            vm.holdHandle(grab)
                            vm.finishLoopDrag(grab)
                        }
                        lifted -> vm.tapHandle(grab)
                    }
                    scrubbing = null
                    onScrub(null)
                    onKeepControls()
                }
            },
    ) {
        val u = ui
        val span = (u.end - u.start).coerceAtLeast(0.001)
        val line = size.width - 2 * insetPx
        fun x(t: Double) = insetPx + ((t - u.start) / span).toFloat().coerceIn(0f, 1f) * line
        val lineBottom = size.height - LANE_HEIGHT.toPx()
        val lineHeight = LINE_HEIGHT.toPx()
        val lineTop = lineBottom - lineHeight
        drawRect(Color(0xFF3A3A4A), Offset(insetPx, lineTop), Size(line, lineHeight))
        for ((from, to) in u.downloadedRuns) drawRect(Color(0xFF5E5E78), Offset(x(from), lineTop), Size(x(to) - x(from), lineHeight))
        val shown = scrubbing ?: u.position
        drawRect(Brand.GradientH, Offset(insetPx, lineTop), Size((x(shown) - insetPx).coerceAtLeast(0f), lineHeight))
        val cutHeight = lineHeight + 2.dp.toPx()
        for (k in u.edit.kept) drawRect(CutColor, Offset(x(k.start), lineBottom - cutHeight), Size(x(k.end) - x(k.start), cutHeight))
        val markerY = lineBottom + LANE_HEIGHT.toPx() / 2
        for (m in u.markers) {
            if (m.kind in u.hiddenKinds) continue
            drawCircle(laneColors[m.kind] ?: Color.White, MARKER_RADIUS.toPx(), Offset(x(m.marker.startSeconds), markerY))
        }
        val loopA = u.loopA
        val loopB = u.loopB
        if (u.paused || scrubbing != null) drawCircle(Color.White, THUMB_RADIUS.toPx(), Offset(x(shown), lineTop + lineHeight / 2))
        if (loopA != null && loopB != null) {
            val thick = LINE_HEIGHT.toPx() + 2.dp.toPx()
            drawRect(LoopColor, Offset(x(loopA), lineBottom - thick), Size(x(loopB) - x(loopA), thick))
            val handleY = lineTop + lineHeight / 2
            val radius = HANDLE_RADIUS.toPx()
            for ((h, pair) in listOf(TimelineGrab.A to (loopA to a), TimelineGrab.B to (loopB to b))) {
                val (t, text) = pair
                val cx = x(t)
                if (u.selectedHandle == h) {
                    // The selected handle grows and gets a white ring, so the tap is seen to have landed.
                    val big = radius * SELECTED_GROWTH
                    drawCircle(LoopColor, big, Offset(cx, handleY))
                    drawCircle(Color.White, big, Offset(cx, handleY), style = Stroke(2.dp.toPx()))
                } else {
                    drawCircle(LoopColor, radius, Offset(cx, handleY))
                }
                drawText(text, topLeft = Offset(cx - text.size.width / 2f, handleY - text.size.height / 2f))
            }
        }
    }
}

@Composable
private fun TimeRow(ui: PlayerUi, onEvents: () -> Unit) {
    Row(Modifier.fillMaxWidth().height(40.dp).padding(horizontal = 16.dp), verticalAlignment = Alignment.CenterVertically) {
        Text(TimeText.of(ui.position - ui.start), Modifier.weight(1f), color = Color.White, style = MaterialTheme.typography.labelMedium)
        val shown = ui.markers.count { it.kind !in ui.hiddenKinds }
        if (ui.markers.isNotEmpty()) {
            Row(Modifier.clip(MaterialTheme.shapes.small).clickable(onClick = onEvents).height(40.dp).padding(horizontal = 8.dp), verticalAlignment = Alignment.CenterVertically) {
                Text(if (shown == 1) "1 event" else "$shown events", color = Color(0xFFB9B9C7), style = MaterialTheme.typography.labelMedium)
                Icon(SppIcons.ExpandMore, null, Modifier.size(18.dp), tint = Color(0xFFB9B9C7))
            }
        }
        Text(TimeText.of((ui.end - ui.start).coerceAtLeast(0.0)), Modifier.weight(1f), color = Color(0xFFB9B9C7), style = MaterialTheme.typography.labelMedium, textAlign = androidx.compose.ui.text.style.TextAlign.End)
    }
}

/** Five equal cells around the middle of the screen, so play stays exactly centred. */
@Composable
private fun TransportRow(vm: PlayerViewModel, ui: PlayerUi, onKeepControls: () -> Unit, onScrub: (Scrub?) -> Unit) {
    val handle = ui.selectedHandle
    if (handle != null && ui.loopA != null) {
        HandleTransportRow(vm, ui, handle, onKeepControls, onScrub)
        return
    }
    Row(Modifier.fillMaxWidth().height(80.dp), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
        TransportCell(SppIcons.PreviousMarker, "Previous marker", enabled = ui.markers.isNotEmpty()) {
            vm.previousEvent()
            onKeepControls()
        }
        TransportCell(SppIcons.FrameBack, "Back one frame") {
            vm.frameBack()
            onKeepControls()
        }
        Box(Modifier.width(TRANSPORT_CELL), contentAlignment = Alignment.Center) {
            Box(
                Modifier.size(64.dp).clip(CircleShape).background(Color(0xFFF2F2F7)).clickable(role = Role.Button) {
                    vm.togglePause()
                    onKeepControls()
                },
                contentAlignment = Alignment.Center,
            ) {
                Icon(playIcon(ui), playLabel(ui), Modifier.size(32.dp), tint = Color(0xFF101018))
            }
        }
        TransportCell(SppIcons.FrameForward, "Forward one frame") {
            vm.frameForward()
            onKeepControls()
        }
        TransportCell(SppIcons.NextMarker, "Next marker", enabled = ui.markers.isNotEmpty()) {
            vm.nextEvent()
            onKeepControls()
        }
    }
}

/**
 * The same five cells while an A or B handle is selected, in the loop's colour: the outer ones jump the handle ±3 s, the arrows move it 15 frames a press and faster while held,
 * and play stays in the middle. The picture follows the handle when the finger lifts.
 */
@Composable
private fun HandleTransportRow(vm: PlayerViewModel, ui: PlayerUi, handle: TimelineGrab, onKeepControls: () -> Unit, onScrub: (Scrub?) -> Unit) {
    val name = handle.name
    val jump = LoopHandles.JUMP_SECONDS.toInt()
    fun step(direction: Int): (Long) -> Unit = { held ->
        vm.nudgeHandle(direction * LoopHandles.heldFrames(held))
        vm.selectedHandleSeconds()?.let { onScrub(Scrub(it, name)) }
        onKeepControls()
    }
    val release: () -> Unit = {
        onScrub(null)
        vm.commitHandle()
        onKeepControls()
    }
    Row(Modifier.fillMaxWidth().height(80.dp), horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
        JumpCell("−$jump s", "Move $name $jump seconds earlier") {
            vm.jumpHandle(-1)
            onKeepControls()
        }
        HoldCell(SppIcons.MoveHandleBack, "Move $name earlier", step(-1), release)
        Box(Modifier.width(TRANSPORT_CELL), contentAlignment = Alignment.Center) {
            Box(
                Modifier.size(64.dp).clip(CircleShape).background(Color(0xFFF2F2F7)).clickable(role = Role.Button) {
                    vm.togglePause()
                    onKeepControls()
                },
                contentAlignment = Alignment.Center,
            ) {
                Icon(playIcon(ui), playLabel(ui), Modifier.size(32.dp), tint = Color(0xFF101018))
            }
        }
        HoldCell(SppIcons.MoveHandleForward, "Move $name later", step(1), release)
        JumpCell("+$jump s", "Move $name $jump seconds later") {
            vm.jumpHandle(1)
            onKeepControls()
        }
    }
}

@Composable
private fun JumpCell(text: String, label: String, onClick: () -> Unit) {
    Box(Modifier.width(TRANSPORT_CELL), contentAlignment = Alignment.Center) {
        Box(
            Modifier.size(48.dp).clip(CircleShape).border(1.5.dp, LoopColor, CircleShape).clickable(role = Role.Button, onClick = onClick).semantics { contentDescription = label },
            contentAlignment = Alignment.Center,
        ) {
            Text(text, color = LoopColor, style = MaterialTheme.typography.labelMedium, maxLines = 1)
        }
    }
}

/** A button that repeats while held: [onStep] gets how long it has been held, [onRelease] runs once when the finger lifts. */
@Composable
private fun HoldCell(icon: ImageVector, label: String, onStep: (Long) -> Unit, onRelease: () -> Unit) {
    val step by rememberUpdatedState(onStep)
    val release by rememberUpdatedState(onRelease)
    val scope = rememberCoroutineScope()
    var pressed by remember { mutableStateOf(false) }
    Box(Modifier.width(TRANSPORT_CELL), contentAlignment = Alignment.Center) {
        Box(
            Modifier.size(48.dp).clip(CircleShape).background(if (pressed) LoopColor.copy(alpha = 0.45f) else LoopColor.copy(alpha = 0.18f))
                .semantics {
                    role = Role.Button
                    contentDescription = label
                    onClick {
                        step(0)
                        release()
                        true
                    }
                }
                .pointerInput(Unit) {
                    awaitEachGesture {
                        val down = awaitFirstDown()
                        down.consume()
                        pressed = true
                        val from = SystemClock.uptimeMillis()
                        step(0)
                        val repeat = scope.launch {
                            delay(LoopHandles.REPEAT_AFTER_MILLIS)
                            while (true) {
                                step(SystemClock.uptimeMillis() - from)
                                delay(LoopHandles.REPEAT_EVERY_MILLIS)
                            }
                        }
                        try {
                            waitForUpOrCancellation()
                        } finally {
                            repeat.cancel()
                            pressed = false
                            release()
                        }
                    }
                },
            contentAlignment = Alignment.Center,
        ) {
            Icon(icon, null, Modifier.size(30.dp), tint = LoopColor)
        }
    }
}

@Composable
private fun TransportCell(icon: ImageVector, label: String, enabled: Boolean = true, onClick: () -> Unit) {
    Box(Modifier.width(TRANSPORT_CELL), contentAlignment = Alignment.Center) {
        IconButton(onClick, Modifier.size(48.dp), enabled = enabled) { Icon(icon, label, Modifier.size(28.dp), tint = if (enabled) Color.White else Color(0x61FFFFFF)) }
    }
}

/** One compact line: A-B loop, sync, sound, speed. The sound chip has two sides: the speaker mutes, the number opens the volume. */
@Composable
private fun OptionsRow(vm: PlayerViewModel, ui: PlayerUi, onSheet: (Sheet) -> Unit) {
    val looping = ui.loopA != null
    Row(Modifier.fillMaxWidth().height(40.dp), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        OptionChip(Modifier.weight(1.05f), if (looping) LoopColor else ChipColor) {
            val fg = if (looping) Color(0xFF2A1600) else Color.White
            Box(Modifier.weight(1f).fillMaxHeight().clickable(role = Role.Button) { if (looping) onSheet(Sheet.Loop) else vm.startLoop() }, contentAlignment = Alignment.Center) {
                Text("A-B", color = fg, style = MaterialTheme.typography.labelLarge)
            }
            if (looping) {
                Box(Modifier.width(30.dp).fillMaxHeight().clickable(role = Role.Button) { vm.clearLoop() }, contentAlignment = Alignment.Center) {
                    Icon(SppIcons.Close, "Reset A-B loop", Modifier.size(18.dp), tint = fg)
                }
            }
        }
        OptionChip(Modifier.weight(0.95f), ChipColor) {
            Row(Modifier.fillMaxSize().clickable(role = Role.Button) { onSheet(Sheet.Sync) }, horizontalArrangement = Arrangement.Center, verticalAlignment = Alignment.CenterVertically) {
                Icon(SppIcons.Sync, null, Modifier.size(18.dp), tint = Color.White)
                Spacer(Modifier.width(4.dp))
                Text("Sync", color = Color.White, style = MaterialTheme.typography.labelLarge)
            }
        }
        OptionChip(Modifier.weight(1.3f), if (ui.edit.muted) Color(0xFF4A2024) else ChipColor) {
            val fg = if (ui.edit.muted) Color(0xFFFFDAD6) else Color.White
            Box(Modifier.weight(1f).fillMaxHeight().clickable(role = Role.Button) { vm.toggleMute() }, contentAlignment = Alignment.Center) {
                Icon(if (ui.edit.muted) SppIcons.VolumeOff else SppIcons.VolumeOn, if (ui.edit.muted) "Unmute" else "Mute", Modifier.size(20.dp), tint = fg)
            }
            Box(Modifier.weight(1f).fillMaxHeight().clickable(role = Role.Button) { onSheet(Sheet.Volume) }.semantics { contentDescription = "Volume ${ui.edit.volumePercent.roundToInt()} percent" }, contentAlignment = Alignment.Center) {
                Text("${ui.edit.volumePercent.roundToInt()}%", color = fg, style = MaterialTheme.typography.labelMedium, maxLines = 1)
            }
        }
        OptionChip(Modifier.weight(0.8f), ChipColor) {
            Box(Modifier.fillMaxSize().clickable(role = Role.Button) { onSheet(Sheet.Speed) }.semantics { contentDescription = "Playback speed ${speedText(ui.edit.speed)}" }, contentAlignment = Alignment.Center) {
                Text(speedText(ui.edit.speed), color = Color.White, style = MaterialTheme.typography.labelLarge, maxLines = 1)
            }
        }
    }
}

private val ChipColor = Color(0xFF1F1F2B)

@Composable
private fun OptionChip(modifier: Modifier, color: Color, content: @Composable androidx.compose.foundation.layout.RowScope.() -> Unit) {
    Row(modifier.fillMaxHeight().clip(MaterialTheme.shapes.medium).background(color), verticalAlignment = Alignment.CenterVertically, content = content)
}

private fun speedText(speed: Double) = "${if (speed % 1.0 == 0.0) speed.toInt().toString() else speed.toString()}×"

/** The quality ladder, the last row of the player: what Export will use. */
@Composable
private fun Ladder(vm: PlayerViewModel, ui: PlayerUi, modifier: Modifier = Modifier) {
    val context = LocalContext.current
    val choice = ui.exportChoice
    val tier = ExportLadder.tier(choice)
    val seconds = vm.outputSeconds(choice)
    val plan = tier?.let { ExportLadder.plan(it, ui.exportEngine, ui.encoders, ui.sourceWidth, ui.sourceHeight, ui.sourceFps, seconds, null) }
    val size = when {
        tier == null -> "about " + Formatter.formatShortFileSize(context, vm.saveClipBytes())
        plan == null -> "not on this phone"
        tier.id == ExportLadder.TARGET_ID -> ""
        else -> "about " + Formatter.formatShortFileSize(context, ExportLadder.estimateBytes(plan, seconds))
    }
    Column(modifier) {
        Row(Modifier.fillMaxWidth().padding(bottom = 6.dp), verticalAlignment = Alignment.CenterVertically) {
            Text(tier?.name ?: "Save clip", color = Color.White, style = MaterialTheme.typography.labelLarge)
            Spacer(Modifier.width(8.dp))
            Text(
                listOf(tier?.tagline ?: "Lossless, no re-encode", size).filter { it.isNotEmpty() }.joinToString(" · "),
                Modifier.weight(1f),
                color = Color(0xFFB9B9C7),
                style = MaterialTheme.typography.bodySmall,
                maxLines = 1,
                overflow = TextOverflow.Ellipsis,
                textAlign = androidx.compose.ui.text.style.TextAlign.End,
            )
        }
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
            LadderTile("✂️", "Clip", "Save clip", choice == ExportLadder.SAVE_CLIP_ID, Modifier.weight(1f)) { vm.setExportChoice(ExportLadder.SAVE_CLIP_ID) }
            ExportLadder.tiers.forEach { t ->
                LadderTile(t.emoji, t.suffix, t.name, choice == t.id, Modifier.weight(1f)) { vm.setExportChoice(t.id) }
            }
        }
    }
}

@Composable
private fun LadderTile(emoji: String, code: String, name: String, selected: Boolean, modifier: Modifier, onClick: () -> Unit) {
    val shape = MaterialTheme.shapes.medium
    Column(
        modifier.height(56.dp).clip(shape).background(if (selected) Color(0xFF24345F) else Color(0xFF14141C))
            .border(1.dp, if (selected) Color(0xFFA9C1FF) else Color(0xFF2E2E3C), shape)
            .selectable(selected, role = Role.RadioButton, onClick = onClick)
            .semantics { contentDescription = name },
        horizontalAlignment = Alignment.CenterHorizontally,
        verticalArrangement = Arrangement.Center,
    ) {
        Text(emoji, fontSize = 18.sp)
        Text(code, color = if (selected) Color(0xFFDCE5FF) else Color(0xFFD5D5E0), style = MaterialTheme.typography.labelSmall)
    }
}

/** Sideways the picture has the screen; a tap brings everything back over it. Export stays top right and the fullscreen icon bottom right. */
@Composable
private fun BoxScope.FullscreenOverlay(ui: PlayerUi, vm: PlayerViewModel, onKeepControls: () -> Unit, onExit: () -> Unit, onBack: () -> Unit, onSheet: (Sheet) -> Unit, onScreenshot: () -> Unit, onScrub: (Scrub?) -> Unit) {
    Box(Modifier.matchParentSize().background(Color(0x4D000000)))
    Row(Modifier.align(Alignment.TopStart).fillMaxWidth().padding(start = 12.dp, end = 16.dp, top = 8.dp), verticalAlignment = Alignment.CenterVertically) {
        IconButton(onBack) { Icon(SppIcons.Back, "Back", tint = Color.White) }
        Text(ui.title, Modifier.weight(1f).padding(horizontal = 4.dp), color = Color.White, maxLines = 1, overflow = TextOverflow.Ellipsis, style = MaterialTheme.typography.titleMedium)
        IconButton(onScreenshot) { Icon(SppIcons.Screenshot, "Screenshot", tint = Color.White) }
        ExportButton { onSheet(Sheet.Export) }
    }
    Box(Modifier.align(Alignment.Center)) { TransportRow(vm, ui, onKeepControls, onScrub) }
    Row(Modifier.align(Alignment.BottomStart).fillMaxWidth().padding(start = 24.dp, end = 12.dp, bottom = 32.dp), verticalAlignment = Alignment.CenterVertically) {
        Text("${TimeText.of(ui.position - ui.start)} / ${TimeText.of((ui.end - ui.start).coerceAtLeast(0.0))}", color = Color.White, style = MaterialTheme.typography.labelMedium)
        Spacer(Modifier.weight(1f))
        Box(Modifier.width(FULLSCREEN_OPTIONS_WIDTH)) { OptionsRow(vm, ui, onSheet) }
        IconButton(onExit) { Icon(SppIcons.FullscreenExit, "Exit fullscreen", tint = Color.White) }
    }
}

@Composable
private fun SheetTitle(text: String) {
    Text(text, style = MaterialTheme.typography.titleLarge)
}

@Composable
private fun SpeedSheet(vm: PlayerViewModel, ui: PlayerUi, close: () -> Unit) {
    SheetTitle("Speed")
    Column {
        for (s in listOf(0.25, 0.5, 1.0, 1.5, 2.0, 4.0)) {
            val selected = abs(ui.edit.speed - s) < 0.001
            Row(
                Modifier.fillMaxWidth().height(48.dp).selectable(selected, role = Role.RadioButton) {
                    vm.setSpeed(s)
                    close()
                },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Text(speedText(s), Modifier.weight(1f), style = MaterialTheme.typography.bodyLarge)
                if (selected) Icon(SppIcons.Check, null, tint = MaterialTheme.colorScheme.primary)
            }
        }
    }
}

@Composable
private fun SyncSheet(vm: PlayerViewModel, ui: PlayerUi) {
    SheetTitle("Sync")
    Button({ vm.detectSync() }, Modifier.fillMaxWidth(), enabled = !ui.sync.running) { Text(if (ui.sync.running) "Detecting…" else "Detect automatically") }
    if (ui.sync.running) LinearProgressIndicator({ ui.sync.progress.toFloat() }, Modifier.fillMaxWidth())
    ui.sync.message?.let { Text(it, style = MaterialTheme.typography.bodyMedium) }
    Row(verticalAlignment = Alignment.CenterVertically) {
        Text("Sound against the picture", Modifier.weight(1f), style = MaterialTheme.typography.bodyMedium)
        Text("${"%+.0f".format(ui.edit.audioDelay * 1000)} ms", style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
    }
    Slider(ui.edit.audioDelay.toFloat(), { vm.setAudioDelay(Math.round(it * 200) / 200.0) }, valueRange = -1.5f..1.5f)
    TextButton({ vm.setAudioDelay(0.0) }) { Text("Reset sync") }
}

@Composable
private fun VolumeSheet(vm: PlayerViewModel, ui: PlayerUi) {
    SheetTitle("Volume")
    Row(verticalAlignment = Alignment.CenterVertically) {
        IconButton({ vm.toggleMute() }) { Icon(if (ui.edit.muted) SppIcons.VolumeOff else SppIcons.VolumeOn, if (ui.edit.muted) "Unmute" else "Mute") }
        Slider(ui.edit.volumePercent.toFloat(), { vm.setVolume(Math.round(it / 5) * 5.0) }, Modifier.weight(1f), valueRange = 0f..130f)
        Text("${ui.edit.volumePercent.roundToInt()} %", Modifier.width(52.dp), style = MaterialTheme.typography.bodyMedium, textAlign = androidx.compose.ui.text.style.TextAlign.End)
    }
}

@Composable
private fun EventsSheet(vm: PlayerViewModel, ui: PlayerUi, close: () -> Unit) {
    SheetTitle("Events")
    val kinds = ui.markers.map { it.kind }.distinct()
    FlowRow(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        kinds.forEach { k -> FilterChip(k !in ui.hiddenKinds, { vm.toggleKind(k) }, { Text(MarkerKinds.emoji(k) + " " + k.name, maxLines = 1) }) }
    }
    val shown = ui.markers.filter { it.kind !in ui.hiddenKinds }
    val now = MarkerNavigation.nowIndex(shown.map { it.marker.startSeconds }, ui.position)
    val window = MarkerNavigation.windowAround(shown.size, now, MAX_LISTED_EVENTS)
    val nowView = remember { BringIntoViewRequester() }
    // Opened, the list goes to where the playhead is, with a few events after it in view too; it then stays where the user scrolls it, and only the line moves.
    val around = with(LocalDensity.current) { NOW_CONTEXT.toPx() }
    LaunchedEffect(Unit) { nowView.bringIntoView(androidx.compose.ui.geometry.Rect(0f, -around, 1f, around)) }
    Column {
        for (i in window.first..window.last + 1) {
            if (i == now) NowLine(ui, Modifier.bringIntoViewRequester(nowView))
            val m = shown.getOrNull(i)?.takeIf { i in window } ?: continue
            Row(
                Modifier.fillMaxWidth().height(48.dp).clickable {
                    vm.seek(MarkerNavigation.landing(m.marker))
                    close()
                },
                verticalAlignment = Alignment.CenterVertically,
            ) {
                Box(Modifier.size(10.dp).background(laneColors[m.kind] ?: Color.White, CircleShape))
                Spacer(Modifier.width(16.dp))
                Text(m.marker.entry.title?.let(::plainText) ?: m.kind.name, Modifier.weight(1f), style = MaterialTheme.typography.bodyLarge, maxLines = 1, overflow = TextOverflow.Ellipsis)
                Text(TimeText.of(m.marker.startSeconds - ui.start), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }
}

/** The "you are here" line between the events before and after the playhead, with the playhead's time. */
@Composable
private fun NowLine(ui: PlayerUi, modifier: Modifier) {
    Row(modifier.fillMaxWidth().height(28.dp).semantics { contentDescription = "Playhead at ${TimeText.of(ui.position - ui.start)}" }, verticalAlignment = Alignment.CenterVertically) {
        Box(Modifier.size(10.dp).background(Color.White, CircleShape))
        Box(Modifier.weight(1f).height(2.dp).background(Brand.GradientH))
        Spacer(Modifier.width(8.dp))
        Text("Now " + TimeText.of(ui.position - ui.start), color = MaterialTheme.colorScheme.primary, style = MaterialTheme.typography.labelMedium)
    }
}

@Composable
private fun LoopSheet(vm: PlayerViewModel, ui: PlayerUi) {
    SheetTitle("A-B loop and cuts")
    Text(
        "A ${TimeText.of((ui.loopA ?: ui.start) - ui.start)} to B ${TimeText.of((ui.loopB ?: ui.end) - ui.start)}. Drag a handle on the timeline, or tap it and move it with the arrows: 15 frames a press, faster while held, and ±3 s with the outer buttons.",
        style = MaterialTheme.typography.bodyMedium,
        color = MaterialTheme.colorScheme.onSurfaceVariant,
    )
    Button({ vm.addLoopToCuts() }, Modifier.fillMaxWidth()) { Text("Add to cuts") }
    if (ui.markers.isNotEmpty()) OutlinedButton({ vm.keepHighlights() }, Modifier.fillMaxWidth()) { Text("Add a cut around every shown event") }
    if (ui.edit.kept.isNotEmpty()) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Cuts to export", Modifier.weight(1f), style = MaterialTheme.typography.titleSmall)
            TextButton({ vm.clearEdit() }) { Text("Clear all") }
        }
        Column {
            ui.edit.kept.forEach { k ->
                Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        "${TimeText.of(k.start - ui.start)} to ${TimeText.of(k.end - ui.start)} (${"%.1f".format(k.end - k.start)} s)",
                        Modifier.weight(1f).clickable { vm.seek(k.start) }.padding(vertical = 12.dp),
                        style = MaterialTheme.typography.bodyMedium,
                    )
                    IconButton({ vm.removeRange(k) }) { Icon(SppIcons.Close, "Remove this cut", tint = MaterialTheme.colorScheme.onSurfaceVariant) }
                }
            }
        }
    }
}

@OptIn(ExperimentalMaterial3Api::class)
@Composable
private fun ExportSheet(vm: PlayerViewModel, ui: PlayerUi, entry: RecordingEntry, library: LibraryUi, onStarted: (Boolean) -> Unit) {
    val context = LocalContext.current
    val game = library.games.firstOrNull { it.game.gameId == entry.gameId }?.game?.name
    var targetMb by remember { mutableStateOf("10") }
    val choice = ui.exportChoice
    val stem = remember(entry.key) {
        val date = DateTimeFormatter.ofPattern("yyyy-MM-dd HH.mm.ss").withZone(ZoneId.systemDefault()).format(entry.startUtc)
        sanitize((entry.title?.takeIf { it.isNotBlank() } ?: game ?: "Recording") + if (entry.title.isNullOrBlank()) " $date" else "")
    }
    SheetTitle("Export")
    val active by Exports.active.collectAsState()
    active?.let {
        Text("${it.request.title}: ${it.stage} ${(it.fraction * 100).toInt()} %", style = MaterialTheme.typography.bodySmall)
        LinearProgressIndicator({ it.fraction.toFloat() }, Modifier.fillMaxWidth())
    }

    Text("What to export", style = MaterialTheme.typography.titleSmall)
    val scope = vm.exportScope()
    Column {
        ExportScopes.available(ui.loopA != null && ui.loopB != null, ui.edit.kept.isNotEmpty()).forEach { s ->
            Row(Modifier.fillMaxWidth().height(48.dp).selectable(s == scope, role = Role.RadioButton) { vm.chooseExportScope(s) }, verticalAlignment = Alignment.CenterVertically) {
                RadioButton(s == scope, null)
                Spacer(Modifier.width(16.dp))
                Text(
                    when (s) {
                        ExportScope.Whole -> "Whole recording"
                        ExportScope.Loop -> "A-B range"
                        ExportScope.Cuts -> "Cuts (${ui.edit.kept.size})"
                    },
                    Modifier.weight(1f),
                    style = MaterialTheme.typography.bodyLarge,
                )
                Text(TimeText.of(vm.scopeSeconds(s)), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
            }
        }
    }

    Text("Quality", style = MaterialTheme.typography.titleSmall)
    Ladder(vm, ui)

    val tier = ExportLadder.tier(choice)
    if (tier != null) {
        Text("Engine", style = MaterialTheme.typography.titleSmall)
        SingleChoiceSegmentedButtonRow(Modifier.fillMaxWidth()) {
            LadderEngine.entries.forEachIndexed { i, e ->
                SegmentedButton(ui.exportEngine == e, { vm.setExportEngine(e) }, SegmentedButtonDefaults.itemShape(i, LadderEngine.entries.size)) {
                    Text(
                        when (e) {
                            LadderEngine.Auto -> "Auto"
                            LadderEngine.Cpu -> "CPU"
                            LadderEngine.Gpu -> "GPU"
                        },
                    )
                }
            }
        }
    }
    Text("Special presets", style = MaterialTheme.typography.titleSmall)
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(6.dp)) {
        ExportLadder.specials.forEach { p ->
            FilterChip(choice == p.id, { vm.setExportChoice(if (choice == p.id) ExportLadder.DEFAULT_ID else p.id) }, { Text(p.name, maxLines = 1) })
        }
    }
    if (tier?.id == ExportLadder.TARGET_ID) {
        OutlinedTextField(targetMb, { targetMb = it }, label = { Text("Size in MB") }, singleLine = true, keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Decimal))
    }
    val target = if (tier?.id == ExportLadder.TARGET_ID) targetMb.toDoubleOrNull() else null
    val seconds = vm.outputSeconds(choice)
    val plan = tier?.let { ExportLadder.plan(it, ui.exportEngine, ui.encoders, ui.sourceWidth, ui.sourceHeight, ui.sourceFps, seconds, target) }
    val summary = when {
        tier == null -> "${"%.1f".format(seconds)} s · about ${Formatter.formatShortFileSize(context, vm.saveClipBytes())} · no re-encode"
        plan == null -> "No encoder on this phone can do this at ${ui.sourceWidth}x${ui.sourceHeight} on this engine."
        else -> "${"%.1f".format(seconds)} s · ${plan.width}x${plan.height} ${codecName(plan.mime)} · about ${Formatter.formatShortFileSize(context, ExportLadder.estimateBytes(plan, seconds))}"
    }
    Text(summary, color = if (tier != null && plan == null) MaterialTheme.colorScheme.error else MaterialTheme.colorScheme.onSurface, style = MaterialTheme.typography.bodyMedium)
    if (tier != null && (ui.edit.speed != 1.0 || ui.edit.muted || ui.edit.volumePercent != 100.0)) {
        Text("Speed, mute and volume apply to this file. Sync applies to every export.", color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
    }
    val ready = tier == null || plan != null
    Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.spacedBy(8.dp)) {
        OutlinedButton({
            vm.export(stem, target, share = true)
            onStarted(true)
        }, Modifier.weight(1f).height(48.dp), enabled = ready) { Text("Export and share", maxLines = 1) }
        Button({
            vm.export(stem, target, share = false)
            onStarted(false)
        }, Modifier.weight(1f).height(48.dp), enabled = ready) { Text("Export") }
    }
    Text("Saved to Movies/SteamPoweredPlayer on this phone. Nothing is written to the share.", color = MaterialTheme.colorScheme.onSurfaceVariant, style = MaterialTheme.typography.bodySmall)
}

/** Steam writes some event titles with XML entities left in them, such as an apostrophe in a player's name. */
private fun plainText(title: String) = title.replace("&apos;", "'").replace("&quot;", "\"").replace("&lt;", "<").replace("&gt;", ">").replace("&amp;", "&")

private fun sanitize(name: String) = name.map { if (it in "\\/:*?\"<>|") ' ' else it }.joinToString("").trim().take(MAX_STEM).ifEmpty { "Export" }

private fun codecName(mime: String) = when (mime) {
    CodecMime.AV1 -> "AV1"
    CodecMime.HEVC -> "HEVC"
    else -> "H.264"
}

private val BUBBLE_WIDTH = 128.dp
private val LANE_HEIGHT = 22.dp
private val LINE_HEIGHT = 3.dp
private val TIMELINE_TOUCH_HEIGHT = 50.dp
private val HANDLE_REACH = 24.dp
private val HANDLE_RADIUS = 13.dp
private const val SELECTED_GROWTH = 1.4f
private val NOW_CONTEXT = 48.dp * 3
private val THUMB_RADIUS = 6.5.dp
private val MARKER_RADIUS = 4.dp
private val TRANSPORT_CELL = 64.dp
private val FULLSCREEN_LINE_INSET = 24.dp
private val FULLSCREEN_OPTIONS_WIDTH = 330.dp
private const val MAX_LISTED_EVENTS = 200
private const val MAX_STEM = 120
private const val FLASH_MILLIS = 700L
private const val CONTROLS_MILLIS = 3000L
private const val DOUBLE_TAP_SECONDS = 5.0
private const val DOUBLE_TAP_GUARD_MILLIS = 300L
private const val SEEK_FLASH_WIDTH = 0.42f
