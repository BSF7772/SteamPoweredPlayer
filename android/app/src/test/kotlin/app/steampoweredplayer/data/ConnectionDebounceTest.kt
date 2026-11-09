// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.data

import org.junit.Assert.assertEquals
import org.junit.Test

class ConnectionDebounceTest {
    private var time = 0L
    private val debounce = ConnectionDebounce(15_000) { time }

    @Test
    fun itStartsConnecting() {
        assertEquals(ConnectionPhase.Connecting, debounce.phase)
    }

    @Test
    fun aFailureInsideTheWindowStaysConnecting() {
        time = 14_999
        assertEquals(ConnectionPhase.Connecting, debounce.failed())
    }

    @Test
    fun aFailureThatOutlastsTheWindowIsReported() {
        time = 15_000
        assertEquals(ConnectionPhase.Failed, debounce.failed())
    }

    @Test
    fun aSuccessClearsAFailureAtOnce() {
        time = 20_000
        debounce.failed()
        debounce.succeeded()
        assertEquals(ConnectionPhase.Connected, debounce.phase)
    }

    @Test
    fun aDropAfterBeingConnectedStartsANewWindow() {
        debounce.succeeded()
        time = 100_000
        assertEquals(ConnectionPhase.Connecting, debounce.failed())
        time = 114_999
        assertEquals(ConnectionPhase.Connecting, debounce.failed())
        time = 115_000
        assertEquals(ConnectionPhase.Failed, debounce.failed())
    }

    @Test
    fun beginningAgainGivesAFailedConnectionAFreshWindow() {
        time = 20_000
        debounce.failed()
        debounce.begin()
        assertEquals(ConnectionPhase.Connecting, debounce.phase)
        time = 34_000
        assertEquals(ConnectionPhase.Connecting, debounce.failed())
    }

    @Test
    fun aFailedConnectionStaysFailedUntilSomethingChanges() {
        time = 20_000
        debounce.failed()
        time = 90_000
        assertEquals(ConnectionPhase.Failed, debounce.failed())
    }
}
