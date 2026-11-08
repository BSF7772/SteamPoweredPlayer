// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.net

import android.content.BroadcastReceiver
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import androidx.core.content.ContextCompat
import java.io.Closeable
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.flow.MutableSharedFlow
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.SharedFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.update

/**
 * Network events, never polled. The default-network callback gives the link's kind and metered flag. A second callback on non-VPN networks
 * reports the real networks coming and going, which a VPN's own network never does (spike S3).
 */
class LinkMonitor(context: Context) : Closeable {
    private val app = context.applicationContext
    private val connectivity = app.getSystemService(ConnectivityManager::class.java)
    private val _state = MutableStateFlow(LinkState(dataSaver = dataSaverOn()))
    private val _changes = MutableSharedFlow<Unit>(replay = 0, extraBufferCapacity = 1, onBufferOverflow = BufferOverflow.DROP_OLDEST)

    val state: StateFlow<LinkState> = _state

    /** A network came up or changed: time to retry whatever was waiting. */
    val changes: SharedFlow<Unit> = _changes

    private val defaultCallback = object : ConnectivityManager.NetworkCallback() {
        override fun onCapabilitiesChanged(network: Network, caps: NetworkCapabilities) {
            _state.update {
                it.copy(
                    connected = true,
                    wired = caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET),
                    metered = !caps.hasCapability(NetworkCapabilities.NET_CAPABILITY_NOT_METERED),
                )
            }
        }

        override fun onLost(network: Network) {
            _state.update { it.copy(connected = false, wired = false, metered = false) }
        }
    }

    private val realNetworks = object : ConnectivityManager.NetworkCallback() {
        override fun onAvailable(network: Network) {
            _changes.tryEmit(Unit)
        }
    }

    private val dataSaver = object : BroadcastReceiver() {
        override fun onReceive(context: Context, intent: Intent) {
            _state.update { it.copy(dataSaver = dataSaverOn()) }
        }
    }

    init {
        connectivity.registerDefaultNetworkCallback(defaultCallback)
        connectivity.registerNetworkCallback(
            NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_NOT_VPN).build(),
            realNetworks,
        )
        ContextCompat.registerReceiver(
            app, dataSaver, IntentFilter(ConnectivityManager.ACTION_RESTRICT_BACKGROUND_CHANGED), ContextCompat.RECEIVER_NOT_EXPORTED,
        )
    }

    private fun dataSaverOn() = connectivity.restrictBackgroundStatus == ConnectivityManager.RESTRICT_BACKGROUND_STATUS_ENABLED

    override fun close() {
        connectivity.unregisterNetworkCallback(defaultCallback)
        connectivity.unregisterNetworkCallback(realNetworks)
        app.unregisterReceiver(dataSaver)
    }
}
