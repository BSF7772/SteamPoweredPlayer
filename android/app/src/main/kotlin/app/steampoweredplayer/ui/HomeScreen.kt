// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import androidx.compose.foundation.clickable
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Box
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.Spacer
import androidx.compose.foundation.layout.WindowInsets
import androidx.compose.foundation.layout.consumeWindowInsets
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.height
import androidx.compose.foundation.layout.imePadding
import androidx.compose.foundation.layout.navigationBarsPadding
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.size
import androidx.compose.foundation.layout.width
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.selection.selectable
import androidx.compose.foundation.selection.toggleable
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.AlertDialog
import androidx.compose.material3.Button
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.HorizontalDivider
import androidx.compose.material3.Icon
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.RadioButton
import androidx.compose.material3.Scaffold
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.LaunchedEffect
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.semantics.Role
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.text.style.TextAlign
import androidx.compose.ui.unit.dp
import app.steampoweredplayer.R
import app.steampoweredplayer.data.ProfileProblem
import app.steampoweredplayer.smb.ConnectionInfo
import app.steampoweredplayer.smb.ConnectionResult
import app.steampoweredplayer.smb.RefusedReason

/** What the last connection attempt came to, in a line for the server's row. Null when nothing was tried. */
@Composable
fun connectionLine(attempt: ConnectionAttempt?): Pair<String, Boolean>? {
    if (attempt == null) return null
    return when (val result = attempt.result) {
        null -> stringResource(R.string.connecting) to false
        is ConnectionResult.Connected -> stringResource(R.string.connected) + " · " + details(result.info) to false
        is ConnectionResult.Refused -> stringResource(
            when (result.reason) {
                RefusedReason.OldDialect -> R.string.refused_dialect
                RefusedReason.NotEncrypted -> R.string.refused_encryption
                RefusedReason.NotSigned -> R.string.refused_signing
            },
        ) to true
        ConnectionResult.Unreachable -> stringResource(R.string.unreachable) to true
        is ConnectionResult.Failed -> stringResource(R.string.connection_failed, result.message) to true
    }
}

@Composable
private fun details(info: ConnectionInfo) = stringResource(R.string.connection_details, info.dialect, info.cipher, stringResource(if (info.signingEnforced) R.string.on else R.string.off))

/** Adding or editing a server. It asks only for what a person knows: the share and folder are found, and the rest waits under Advanced. */
@Composable
fun ServerEditorScreen(vm: ServersViewModel, onClose: () -> Unit) {
    val state by vm.state.collectAsState()
    val initial = state.editing
    // Saving or removing clears the editor: this screen's work is done.
    LaunchedEffect(initial == null) { if (initial == null) onClose() }
    if (initial == null) return

    var name by remember(initial.id) { mutableStateOf(initial.name) }
    var host by remember(initial.id) { mutableStateOf(initial.host) }
    var user by remember(initial.id) { mutableStateOf(initial.user) }
    var password by remember(initial.id) { mutableStateOf("") }
    var share by remember(initial.id) { mutableStateOf(initial.share) }
    var path by remember(initial.id) { mutableStateOf(initial.path) }
    var domain by remember(initial.id) { mutableStateOf(initial.domain) }
    var requireEncryption by remember(initial.id) { mutableStateOf(initial.requireEncryption) }
    var advanced by remember(initial.id) { mutableStateOf(false) }
    var confirmRemove by remember(initial.id) { mutableStateOf(false) }
    val existing = state.profiles.any { it.id == initial.id }

    val draft = initial.copy(name = name.trim(), host = host.trim(), share = share.trim(), path = path.trim(), user = user.trim(), domain = domain.trim(), requireEncryption = requireEncryption)
    // The share may stay empty here: the scan finds it.
    val problems = draft.copy(share = draft.share.ifEmpty { "found" }).problems()
    val cancel = { vm.cancelEdit() }
    androidx.activity.compose.BackHandler {
        if (state.setup == ServerSetup.Form) cancel() else vm.backToForm()
    }

    Scaffold(
        containerColor = MaterialTheme.colorScheme.background,
        contentWindowInsets = WindowInsets(0),
        topBar = { SppTopBar(stringResource(if (existing) R.string.edit else R.string.add_server), onBack = { if (state.setup == ServerSetup.Form) cancel() else vm.backToForm() }) },
        bottomBar = {
            Surface(color = MaterialTheme.colorScheme.background) {
                Column(Modifier.imePadding().navigationBarsPadding()) {
                    HorizontalDivider(color = MaterialTheme.colorScheme.surfaceVariant)
                    Box(Modifier.padding(horizontal = 16.dp, vertical = 12.dp)) {
                        when (state.setup) {
                            ServerSetup.Form -> Button({ vm.connect(draft, password) }, Modifier.fillMaxWidth().height(48.dp), enabled = problems.isEmpty()) { Text(stringResource(R.string.connect)) }
                            ServerSetup.Scanning -> OutlinedButton({ vm.backToForm() }, Modifier.fillMaxWidth().height(48.dp)) { Text(stringResource(R.string.cancel)) }
                            is ServerSetup.Found -> Button({ vm.finishSetup() }, Modifier.fillMaxWidth().height(48.dp)) { Text(stringResource(R.string.open_library)) }
                        }
                    }
                }
            }
        },
    ) { padding ->
        val body = Modifier.fillMaxSize().padding(padding).consumeWindowInsets(padding)
        when (val setup = state.setup) {
            ServerSetup.Scanning -> Column(body.padding(24.dp), verticalArrangement = Arrangement.spacedBy(16.dp, Alignment.CenterVertically), horizontalAlignment = Alignment.CenterHorizontally) {
                CircularProgressIndicator(Modifier.size(48.dp))
                Text(stringResource(R.string.looking_for_shares), style = MaterialTheme.typography.bodyLarge, textAlign = TextAlign.Center)
            }
            is ServerSetup.Found -> Column(body.verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp)) {
                val any = setup.shares.any { it.hasRecordings }
                Text(if (any) "Connected. Found your recordings:" else "Connected, but no recordings were found. Choose a share, or type its folder under Advanced.", style = MaterialTheme.typography.bodyLarge)
                Spacer(Modifier.height(8.dp))
                setup.shares.forEach { found ->
                    val selected = found.name == setup.selected
                    Row(
                        Modifier.fillMaxWidth().selectable(selected, role = Role.RadioButton) { vm.chooseShare(found.name) }.padding(vertical = 8.dp),
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        RadioButton(selected, null)
                        Spacer(Modifier.width(16.dp))
                        SppRowText(found.name, if (found.hasRecordings) "Steam recordings found" else "No recordings here", Modifier.weight(1f))
                    }
                }
            }
            ServerSetup.Form -> Column(body.imePadding().verticalScroll(rememberScrollState()).padding(horizontal = 16.dp, vertical = 8.dp), verticalArrangement = Arrangement.spacedBy(12.dp)) {
                Field(R.string.field_name, name) { name = it }
                Field(R.string.field_host, host, isError = host.isNotEmpty() && ProfileProblem.BadHost in problems, keyboard = KeyboardOptions(keyboardType = KeyboardType.Uri)) { host = it }
                Field(R.string.field_user, user) { user = it }
                OutlinedTextField(
                    password, { password = it }, Modifier.fillMaxWidth(), singleLine = true,
                    label = { Text(stringResource(R.string.field_password)) },
                    placeholder = if (existing) ({ Text("Unchanged") }) else null,
                    visualTransformation = PasswordVisualTransformation(),
                    keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
                )
                state.problem?.let { Text(it, color = MaterialTheme.colorScheme.error, style = MaterialTheme.typography.bodyMedium) }
                Row(
                    Modifier.fillMaxWidth().clickable { advanced = !advanced }.height(48.dp),
                    verticalAlignment = Alignment.CenterVertically,
                ) {
                    Text(stringResource(R.string.advanced), Modifier.weight(1f), style = MaterialTheme.typography.titleSmall)
                    Icon(if (advanced) SppIcons.ExpandLess else SppIcons.ExpandMore, null)
                }
                if (advanced) {
                    Field(R.string.field_share, share, isError = share.isNotEmpty() && ProfileProblem.BadShare in problems) { share = it }
                    Field(R.string.field_path, path, isError = ProfileProblem.BadPath in problems) { path = it }
                    Field(R.string.field_domain, domain) { domain = it }
                    Row(
                        Modifier.fillMaxWidth().toggleable(!requireEncryption, role = Role.Switch) { requireEncryption = !it },
                        verticalAlignment = Alignment.CenterVertically,
                    ) {
                        SppRowText(
                            "Allow unencrypted connection",
                            if (requireEncryption) "Off: only encrypted connections are accepted." else stringResource(R.string.unencrypted_warning),
                            Modifier.weight(1f).padding(end = 16.dp),
                            detailColor = if (requireEncryption) MaterialTheme.colorScheme.onSurfaceVariant else MaterialTheme.colorScheme.error,
                        )
                        Switch(!requireEncryption, null)
                    }
                }
                if (existing) TextButton({ confirmRemove = true }) { Text(stringResource(R.string.remove), color = MaterialTheme.colorScheme.error) }
            }
        }
    }
    if (confirmRemove) {
        AlertDialog(
            onDismissRequest = { confirmRemove = false },
            title = { Text("Remove ${initial.name}?") },
            text = { Text("Its saved password is deleted from this phone. Your recordings stay on the server.") },
            confirmButton = { TextButton({ vm.remove(initial) }) { Text(stringResource(R.string.remove)) } },
            dismissButton = { TextButton({ confirmRemove = false }) { Text(stringResource(R.string.cancel)) } },
        )
    }
}

@Composable
private fun Field(label: Int, value: String, isError: Boolean = false, keyboard: KeyboardOptions = KeyboardOptions.Default, onChange: (String) -> Unit) = OutlinedTextField(
    value, onChange, Modifier.fillMaxWidth(), singleLine = true, isError = isError, label = { Text(stringResource(label)) }, keyboardOptions = keyboard,
)
