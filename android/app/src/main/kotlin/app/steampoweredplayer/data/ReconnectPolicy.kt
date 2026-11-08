// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import kotlin.math.min
import kotlin.math.pow
import kotlin.random.Random

enum class ReconnectState { Connected, Reconnecting, TimedOut }
enum class TimedOutAction { None, TryNextProfile, CannotReach }

/**
 * Reconnecting to a remote library, as in the desktop's `Core/Net/ReconnectPolicy` (plan §4.3). No I/O, timers or polling: the caller reports
 * events and schedules its own wake-up. [nowMillis] is a monotonic clock. Each profile gets 30 s of continuous failure; backoff doubles from 1 s to
 * 10 s with jitter in [0.5, 1); a network change retries now, restarts the clock and goes back to the first profile. Drive it from one context.
 */
class ReconnectPolicy(private val nowMillis: () -> Long, private val random: Random, private val profileCount: Int = 1) {
    init {
        require(profileCount >= 1)
    }

    var profileIndex = 0
        private set

    /** The backoff chosen after the latest failure, 0 before any. */
    var currentDelayMillis = 0L
        private set

    private var current = ReconnectState.Connected
    private var outageStart = 0L
    private var nextAttemptAt = 0L
    private var failures = 0

    val state: ReconnectState
        get() {
            refresh()
            return current
        }

    val action: TimedOutAction
        get() = when {
            state != ReconnectState.TimedOut -> TimedOutAction.None
            profileIndex + 1 < profileCount -> TimedOutAction.TryNextProfile
            else -> TimedOutAction.CannotReach
        }

    val millisUntilNextAttempt: Long get() = remaining(nextAttemptAt)
    val millisUntilTimeout: Long get() = if (current == ReconnectState.Connected) TIMEOUT_MILLIS else remaining(outageStart + TIMEOUT_MILLIS)

    fun onDisconnected() {
        refresh()
        if (current == ReconnectState.Connected) beginOutage()
    }

    fun onAttemptFailed() {
        refresh()
        if (current != ReconnectState.Reconnecting) return
        failures++
        val capped = min(BASE_BACKOFF_MILLIS * 2.0.pow(failures - 1), MAX_BACKOFF_MILLIS.toDouble())
        currentDelayMillis = (capped * (0.5 + 0.5 * random.nextDouble())).toLong()
        nextAttemptAt = nowMillis() + currentDelayMillis
        refresh()
    }

    /** The server answered, which also counts after a timeout, for an attempt still in flight. Keeps the profile. */
    fun onConnected() {
        current = ReconnectState.Connected
        failures = 0
        currentDelayMillis = 0
    }

    fun onNetworkChanged() {
        refresh()
        if (current == ReconnectState.Connected) return
        profileIndex = 0
        beginOutage()
    }

    fun tryNextProfile() {
        if (action != TimedOutAction.TryNextProfile) return
        profileIndex++
        beginOutage()
    }

    private fun beginOutage() {
        current = ReconnectState.Reconnecting
        outageStart = nowMillis()
        nextAttemptAt = outageStart
        failures = 0
        currentDelayMillis = 0
    }

    private fun refresh() {
        if (current == ReconnectState.Reconnecting && nowMillis() - outageStart >= TIMEOUT_MILLIS) current = ReconnectState.TimedOut
    }

    private fun remaining(deadline: Long) = (deadline - nowMillis()).coerceAtLeast(0)

    companion object {
        const val TIMEOUT_MILLIS = 30_000L
        const val BASE_BACKOFF_MILLIS = 1_000L
        const val MAX_BACKOFF_MILLIS = 10_000L
    }
}
