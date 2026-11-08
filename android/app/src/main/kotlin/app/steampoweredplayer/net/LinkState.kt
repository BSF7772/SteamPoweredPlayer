// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.net

/** What the current default network is, as far as downloads care (plan §4.3). Pure. */
data class LinkState(
    val connected: Boolean = false,
    val wired: Boolean = false,
    val metered: Boolean = false,
    val dataSaver: Boolean = false,
) {
    /** A transfer on this link gets the one notice. */
    val noticeApplies: Boolean get() = connected && (metered || dataSaver)
}

/**
 * The one toast-style notice for a metered network or Data Saver: once per download, and never again after "Don't show again". The
 * app does nothing else about it: no cap, no prompt, no pause (maintainer 2026-11-07).
 */
class MeteredNoticeGate(private val dismissedForever: () -> Boolean) {
    private var shown = false

    /** True the first time a running download sees a link the notice applies to. */
    fun shouldShow(link: LinkState): Boolean {
        if (shown || !link.noticeApplies || dismissedForever()) return false
        shown = true
        return true
    }
}
