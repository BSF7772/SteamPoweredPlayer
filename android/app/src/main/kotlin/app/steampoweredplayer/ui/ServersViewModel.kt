// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.ui

import android.app.Application
import androidx.lifecycle.AndroidViewModel
import androidx.lifecycle.viewModelScope
import app.steampoweredplayer.data.ProfileStore
import app.steampoweredplayer.data.ServerProfile
import app.steampoweredplayer.smb.ConnectionResult
import app.steampoweredplayer.smb.PasswordVault
import app.steampoweredplayer.smb.SmbClient
import java.io.File
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

/** A profile being tried: result is null while the attempt runs. */
data class ConnectionAttempt(val profileId: String, val result: ConnectionResult?)

data class ServersState(val profiles: List<ServerProfile> = emptyList(), val attempt: ConnectionAttempt? = null, val editing: ServerProfile? = null)

class ServersViewModel(app: Application) : AndroidViewModel(app) {
    private val store = ProfileStore(File(app.filesDir, "servers.json"))
    private val vault = PasswordVault(File(app.noBackupFilesDir, "vault"))
    private val _state = MutableStateFlow(ServersState())
    val state: StateFlow<ServersState> = _state

    init {
        viewModelScope.launch {
            val saved = withContext(Dispatchers.IO) { store.load() }
            _state.update { it.copy(profiles = saved) }
        }
    }

    fun add() = _state.update {
        it.copy(editing = ServerProfile(id = UUID.randomUUID().toString(), name = "", host = "", share = ""))
    }

    fun edit(profile: ServerProfile) = _state.update { it.copy(editing = profile) }

    fun cancelEdit() = _state.update { it.copy(editing = null) }

    /** Saves the profile and then tries it. A blank [password] keeps the stored one. */
    fun save(profile: ServerProfile, password: String) {
        if (profile.problems().isNotEmpty()) return
        val next = _state.value.profiles.filterNot { it.id == profile.id } + profile
        _state.update { it.copy(profiles = next, editing = null) }
        viewModelScope.launch(Dispatchers.IO) {
            if (password.isNotEmpty()) vault.put(profile.id, password)
            store.save(next)
            attempt(profile)
        }
    }

    fun remove(profile: ServerProfile) {
        val next = _state.value.profiles.filterNot { it.id == profile.id }
        _state.update { s -> s.copy(profiles = next, attempt = s.attempt?.takeUnless { it.profileId == profile.id }) }
        viewModelScope.launch(Dispatchers.IO) {
            vault.remove(profile.id)
            store.save(next)
        }
    }

    fun tryConnection(profile: ServerProfile) {
        viewModelScope.launch(Dispatchers.IO) { attempt(profile) }
    }

    private fun attempt(profile: ServerProfile) {
        _state.update { it.copy(attempt = ConnectionAttempt(profile.id, null)) }
        val result = SmbClient.tryConnect(profile, vault.get(profile.id).orEmpty())
        _state.update { it.copy(attempt = ConnectionAttempt(profile.id, result)) }
    }
}
