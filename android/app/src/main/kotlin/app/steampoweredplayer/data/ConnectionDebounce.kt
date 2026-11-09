// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

enum class ConnectionPhase { Connected, Connecting, Failed }

/**
 * Keeps a network switch from reading as an error: a failure inside the window after the connection was last good is still "connecting", and only one that outlasts it is
 * "failed". A success clears everything at once. Pure, with the clock passed in.
 */
class ConnectionDebounce(private val windowMillis: Long = DEFAULT_WINDOW_MILLIS, private val now: () -> Long) {
    var phase = ConnectionPhase.Connecting
        private set
    private var startedAt = now()

    /** A new attempt from scratch: a server switch, a retry, a network change after a failure. */
    fun begin() {
        phase = ConnectionPhase.Connecting
        startedAt = now()
    }

    fun succeeded() {
        phase = ConnectionPhase.Connected
    }

    fun failed(): ConnectionPhase {
        when (phase) {
            ConnectionPhase.Connected -> begin()
            ConnectionPhase.Connecting -> if (now() - startedAt >= windowMillis) phase = ConnectionPhase.Failed
            ConnectionPhase.Failed -> Unit
        }
        return phase
    }

    companion object {
        const val DEFAULT_WINDOW_MILLIS = 15_000L
    }
}
