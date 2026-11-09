// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.steampoweredplayer.R
import app.steampoweredplayer.data.FoundShare
import app.steampoweredplayer.data.ProfileStore
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.data.ShareScan
import app.steampoweredplayer.smb.ConnectionResult
import app.steampoweredplayer.smb.PasswordVault
import app.steampoweredplayer.smb.RefusedReason
import app.steampoweredplayer.smb.SmbClient
import app.steampoweredplayer.smb.SmbRemoteFiles
import java.io.File
import java.io.IOException
import java.util.UUID
import kotlin.coroutines.coroutineContext
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.async
import kotlinx.coroutines.awaitAll
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** A profile being tried: result is null while the attempt runs. */
data class ConnectionAttempt(val profileId: String, val result: ConnectionResult?)

/** Where adding a server has got to, once Connect was pressed. */
sealed interface ServerSetup {
    data object Form : ServerSetup
    data object Scanning : ServerSetup
    data class Found(val shares: List<FoundShare>, val selected: String) : ServerSetup
}

data class ServersState(
    val profiles: List<ServerProfile> = emptyList(),
    val loaded: Boolean = false,
    val attempt: ConnectionAttempt? = null,
    val editing: ServerProfile? = null,
    val setup: ServerSetup = ServerSetup.Form,
    val problem: String? = null,
)

class ServersViewModel(app: Application) : AndroidViewModel(app) {
    private val store = ProfileStore(File(app.filesDir, "servers.json"))
    private val vault = PasswordVault(File(app.noBackupFilesDir, "vault"))
    private val _state = MutableStateFlow(ServersState())
    val state: StateFlow<ServersState> = _state
    private var scan: Job? = null
    private var scanned: ServerProfile? = null
    private var scannedPassword = ""

    init {
        viewModelScope.launch {
            val saved = withContext(Dispatchers.IO) { store.load() }
            _state.update { it.copy(profiles = saved, loaded = true) }
        }
    }

    fun add() = _state.update {
        it.copy(editing = ServerProfile(id = UUID.randomUUID().toString(), name = "", host = "", share = ""), setup = ServerSetup.Form, problem = null)
    }

    fun edit(profile: ServerProfile) = _state.update { it.copy(editing = profile, setup = ServerSetup.Form, problem = null) }

    fun cancelEdit() {
        cancelScan()
        forgetScan()
        _state.update { it.copy(editing = null, setup = ServerSetup.Form, problem = null) }
    }

    /** Back to the form from the scan or its results. */
    fun backToForm() {
        cancelScan()
        forgetScan()
        _state.update { it.copy(setup = ServerSetup.Form) }
    }

    private fun cancelScan() {
        scan?.cancel()
        scan = null
    }

    private fun forgetScan() {
        scanned = null
        scannedPassword = ""
    }

    /** Connect on the form: a share typed under Advanced is used as it is, otherwise the server is asked for its shares and they're searched for recordings. A blank [password] keeps the stored one. */
    fun connect(draft: ServerProfile, password: String) {
        if (draft.share.isNotBlank()) {
            save(draft, password)
            return
        }
        cancelScan()
        _state.update { it.copy(setup = ServerSetup.Scanning, problem = null) }
        scan = viewModelScope.launch(Dispatchers.IO) {
            val secret = password.ifEmpty { vault.get(draft.id).orEmpty() }
            val outcome = scanFor(draft, secret)
            if (!coroutineContext.isActive) return@launch
            val found = outcome.first
            if (found == null) {
                _state.update { it.copy(setup = ServerSetup.Form, problem = outcome.second) }
            } else {
                scanned = draft
                scannedPassword = password
                _state.update { it.copy(setup = ServerSetup.Found(found, (ShareScan.preferred(found) ?: found.first()).name), problem = null) }
            }
        }
    }

    private suspend fun scanFor(draft: ServerProfile, password: String): Pair<List<FoundShare>?, String?> {
        val text = { id: Int -> getApplication<Application>().getString(id) }
        if (!SmbClient.reachable(draft)) return null to text(R.string.unreachable)
        val names = try {
            ShareScan.visible(SmbClient.shares(draft, password))
        } catch (_: IOException) {
            return null to text(R.string.sign_in_failed)
        }
        if (names.isEmpty()) return null to text(R.string.no_shares)
        // The encryption policy is held against a real session before anything is read from the shares.
        for (name in names) {
            when (val tried = SmbClient.tryConnect(draft.copy(share = name), password)) {
                is ConnectionResult.Refused -> return null to text(
                    when (tried.reason) {
                        RefusedReason.OldDialect -> R.string.refused_dialect
                        RefusedReason.NotEncrypted -> R.string.refused_encryption
                        RefusedReason.NotSigned -> R.string.refused_signing
                    },
                )
                is ConnectionResult.Connected -> break
                else -> continue                                                      // this user may not be allowed into that share: try the next one
            }
        }
        return coroutineScope {
            names.map { name ->
                async(Dispatchers.IO) {
                    val active = coroutineContext
                    val files = SmbRemoteFiles(draft.copy(share = name, path = ""), password)
                    FoundShare(name, ShareScan.findRecordings(files) { active.isActive })
                }
            }.awaitAll()
        } to null
    }

    fun chooseShare(name: String) = _state.update { s ->
        val setup = s.setup as? ServerSetup.Found ?: return@update s
        s.copy(setup = setup.copy(selected = name))
    }

    /** Saves the server with the share the scan found. */
    fun finishSetup() {
        val draft = scanned ?: return
        val setup = _state.value.setup as? ServerSetup.Found ?: return
        val share = setup.shares.firstOrNull { it.name == setup.selected } ?: return
        save(draft.copy(share = share.name, path = share.recordingsPath.orEmpty()), scannedPassword)
    }

    /** Saves the profile and then tries it. A blank [password] keeps the stored one. */
    private fun save(profile: ServerProfile, password: String) {
        if (profile.problems().isNotEmpty()) return
        forgetScan()
        val next = _state.value.profiles.filterNot { it.id == profile.id } + profile
        // The file first, then the state: the library reloads from the file as soon as the list changes, and must find the new server there.
        viewModelScope.launch(Dispatchers.IO) {
            if (password.isNotEmpty()) vault.put(profile.id, password)
            store.save(next)
            _state.update { it.copy(profiles = next, editing = null, setup = ServerSetup.Form, problem = null) }
            attempt(profile)
        }
    }

    fun remove(profile: ServerProfile) {
        cancelScan()
        forgetScan()
        val next = _state.value.profiles.filterNot { it.id == profile.id }
        viewModelScope.launch(Dispatchers.IO) {
            vault.remove(profile.id)
            store.save(next)
            _state.update { s -> s.copy(profiles = next, editing = null, setup = ServerSetup.Form, problem = null, attempt = s.attempt?.takeUnless { it.profileId == profile.id }) }
        }
    }

    private fun attempt(profile: ServerProfile) {
        _state.update { it.copy(attempt = ConnectionAttempt(profile.id, null)) }
        val result = SmbClient.tryConnect(profile, vault.get(profile.id).orEmpty())
        _state.update { it.copy(attempt = ConnectionAttempt(profile.id, result)) }
    }
}
