// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import androidx.compose.foundation.background
import androidx.compose.foundation.layout.Arrangement
import androidx.compose.foundation.layout.Column
import androidx.compose.foundation.layout.Row
import androidx.compose.foundation.layout.fillMaxSize
import androidx.compose.foundation.layout.fillMaxWidth
import androidx.compose.foundation.layout.padding
import androidx.compose.foundation.layout.safeDrawingPadding
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.text.KeyboardOptions
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.Button
import androidx.compose.material3.Card
import androidx.compose.material3.CardDefaults
import androidx.compose.material3.CircularProgressIndicator
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.OutlinedButton
import androidx.compose.material3.OutlinedTextField
import androidx.compose.material3.Surface
import androidx.compose.material3.Switch
import androidx.compose.material3.Text
import androidx.compose.material3.TextButton
import androidx.compose.runtime.Composable
import androidx.compose.runtime.collectAsState
import androidx.compose.runtime.getValue
import androidx.compose.runtime.mutableStateOf
import androidx.compose.runtime.remember
import androidx.compose.runtime.setValue
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.graphics.Color
import androidx.compose.ui.res.stringResource
import androidx.compose.ui.text.font.FontWeight
import androidx.compose.ui.text.input.KeyboardType
import androidx.compose.ui.text.input.PasswordVisualTransformation
import androidx.compose.ui.unit.dp
import androidx.lifecycle.viewmodel.compose.viewModel
import app.steampoweredplayer.R
import app.steampoweredplayer.data.ProfileProblem
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.smb.ConnectionInfo
import app.steampoweredplayer.smb.ConnectionResult
import app.steampoweredplayer.smb.RefusedReason
import app.steampoweredplayer.ui.theme.Brand

@Composable
fun HomeScreen(vm: ServersViewModel = viewModel()) {
    val state by vm.state.collectAsState()
    Surface(Modifier.fillMaxSize(), color = MaterialTheme.colorScheme.background) {
        Column(
            Modifier.safeDrawingPadding().padding(16.dp).verticalScroll(rememberScrollState()),
            verticalArrangement = Arrangement.spacedBy(16.dp),
        ) {
            Column(Modifier.fillMaxWidth().background(Brand.Gradient, MaterialTheme.shapes.large).padding(20.dp)) {
                Text(stringResource(R.string.app_name), color = Color.White, style = MaterialTheme.typography.titleLarge, fontWeight = FontWeight.SemiBold)
            }
            val editing = state.editing
            if (editing != null) {
                ProfileEditor(editing, onSave = vm::save, onCancel = vm::cancelEdit)
            } else {
                if (state.profiles.isEmpty()) {
                    SppCard {
                        Text(stringResource(R.string.home_empty_title), style = MaterialTheme.typography.titleMedium)
                        Text(stringResource(R.string.home_empty_body), style = MaterialTheme.typography.bodyMedium, color = MaterialTheme.colorScheme.onSurfaceVariant)
                    }
                }
                state.profiles.forEach { profile ->
                    ProfileCard(profile, state.attempt?.takeIf { it.profileId == profile.id }, vm)
                }
                Button(vm::add) { Text(stringResource(R.string.add_server)) }
            }
        }
    }
}

@Composable
private fun SppCard(content: @Composable () -> Unit) {
    Card(
        Modifier.fillMaxWidth(),
        colors = CardDefaults.cardColors(containerColor = MaterialTheme.colorScheme.surfaceContainer),
        shape = MaterialTheme.shapes.large,
    ) {
        Column(Modifier.padding(16.dp), verticalArrangement = Arrangement.spacedBy(8.dp)) { content() }
    }
}

@Composable
private fun ProfileCard(profile: ServerProfile, attempt: ConnectionAttempt?, vm: ServersViewModel) {
    SppCard {
        Text(profile.name, style = MaterialTheme.typography.titleMedium)
        Text("${profile.host} · ${profile.share}", style = MaterialTheme.typography.bodySmall, color = MaterialTheme.colorScheme.onSurfaceVariant)
        when {
            attempt == null -> Unit
            attempt.result == null -> Row(verticalAlignment = Alignment.CenterVertically, horizontalArrangement = Arrangement.spacedBy(8.dp)) {
                CircularProgressIndicator(Modifier.padding(2.dp), strokeWidth = 2.dp)
                Text(stringResource(R.string.connecting))
            }
            else -> ConnectionSummary(attempt.result)
        }
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            OutlinedButton({ vm.tryConnection(profile) }, enabled = attempt?.let { it.result != null } ?: true) { Text(stringResource(R.string.try_connection)) }
            TextButton({ vm.edit(profile) }) { Text(stringResource(R.string.edit)) }
            TextButton({ vm.remove(profile) }) { Text(stringResource(R.string.remove)) }
        }
    }
}

@Composable
private fun ConnectionSummary(result: ConnectionResult) {
    when (result) {
        is ConnectionResult.Connected -> {
            Text(stringResource(R.string.connected), color = MaterialTheme.colorScheme.tertiary)
            Details(result.info)
            if (result.verdict.unencrypted) Text(stringResource(R.string.unencrypted_warning), color = Brand.Live)
        }
        is ConnectionResult.Refused -> {
            Text(
                stringResource(
                    when (result.reason) {
                        RefusedReason.OldDialect -> R.string.refused_dialect
                        RefusedReason.NotEncrypted -> R.string.refused_encryption
                        RefusedReason.NotSigned -> R.string.refused_signing
                    },
                ),
                color = Brand.Live,
            )
            Details(result.info)
        }
        ConnectionResult.Unreachable -> Text(stringResource(R.string.unreachable), color = Brand.Live)
        is ConnectionResult.Failed -> Text(stringResource(R.string.connection_failed, result.message), color = Brand.Live)
    }
}

@Composable
private fun Details(info: ConnectionInfo) = Text(
    stringResource(R.string.connection_details, info.dialect, info.cipher, stringResource(if (info.signingEnforced) R.string.on else R.string.off)),
    style = MaterialTheme.typography.bodySmall,
    color = MaterialTheme.colorScheme.onSurfaceVariant,
)

@Composable
private fun ProfileEditor(initial: ServerProfile, onSave: (ServerProfile, String) -> Unit, onCancel: () -> Unit) {
    var name by remember { mutableStateOf(initial.name) }
    var host by remember { mutableStateOf(initial.host) }
    var share by remember { mutableStateOf(initial.share) }
    var path by remember { mutableStateOf(initial.path) }
    var user by remember { mutableStateOf(initial.user) }
    var domain by remember { mutableStateOf(initial.domain) }
    var password by remember { mutableStateOf("") }
    var requireEncryption by remember { mutableStateOf(initial.requireEncryption) }
    val draft = initial.copy(
        name = name.trim(), host = host.trim(), share = share.trim(), path = path.trim(), user = user.trim(), domain = domain.trim(),
        requireEncryption = requireEncryption,
    )
    val problems = draft.problems()
    SppCard {
        Field(R.string.field_name, name) { name = it }
        Field(R.string.field_host, host, isError = host.isNotEmpty() && ProfileProblem.BadHost in problems) { host = it }
        Field(R.string.field_share, share, isError = share.isNotEmpty() && ProfileProblem.BadShare in problems) { share = it }
        Field(R.string.field_path, path, isError = ProfileProblem.BadPath in problems) { path = it }
        Field(R.string.field_user, user) { user = it }
        Field(R.string.field_domain, domain) { domain = it }
        OutlinedTextField(
            password, { password = it }, Modifier.fillMaxWidth(), singleLine = true,
            label = { Text(stringResource(R.string.field_password)) },
            visualTransformation = PasswordVisualTransformation(),
            keyboardOptions = KeyboardOptions(keyboardType = KeyboardType.Password),
        )
        Row(Modifier.fillMaxWidth(), verticalAlignment = Alignment.CenterVertically) {
            Text(stringResource(R.string.field_require_encryption), Modifier.weight(1f))
            Switch(requireEncryption, { requireEncryption = it })
        }
        if (!requireEncryption) Text(stringResource(R.string.unencrypted_warning), color = Brand.Live, style = MaterialTheme.typography.bodySmall)
        Row(horizontalArrangement = Arrangement.spacedBy(8.dp)) {
            Button({ onSave(draft, password) }, enabled = problems.isEmpty()) { Text(stringResource(R.string.save_and_try)) }
            TextButton(onCancel) { Text(stringResource(R.string.cancel)) }
        }
    }
}

@Composable
private fun Field(label: Int, value: String, isError: Boolean = false, onChange: (String) -> Unit) = OutlinedTextField(
    value, onChange, Modifier.fillMaxWidth(), singleLine = true, isError = isError, label = { Text(stringResource(label)) },
)
