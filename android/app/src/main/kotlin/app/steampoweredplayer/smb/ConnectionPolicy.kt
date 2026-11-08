// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.smb

/** What a session really negotiated, shown on the connection screen. */
data class ConnectionInfo(val dialect: String, val encrypted: Boolean, val cipher: String, val signingEnforced: Boolean)

sealed interface ConnectionVerdict {
    /** Fine to use. [unencrypted] = the user's "allow unencrypted" override is in effect: the UI warns. */
    data class Accepted(val unencrypted: Boolean) : ConnectionVerdict
    data class Refused(val reason: RefusedReason) : ConnectionVerdict
}

enum class RefusedReason { OldDialect, NotEncrypted, NotSigned }

/**
 * Plan §4.6: dialect 3.0 or newer, encryption required by default, signing on, never silently downgraded. jcifs can't require encryption
 * by itself (it encrypts when the server or share asks for it), so the session is checked after it connects. Pure.
 */
object ConnectionPolicy {
    fun evaluate(info: ConnectionInfo, requireEncryption: Boolean): ConnectionVerdict {
        val major = info.dialect.firstOrNull { it.isDigit() }?.digitToInt() ?: 0
        return when {
            major < 3 -> ConnectionVerdict.Refused(RefusedReason.OldDialect)
            requireEncryption && !info.encrypted -> ConnectionVerdict.Refused(RefusedReason.NotEncrypted)
            !info.signingEnforced && !info.encrypted -> ConnectionVerdict.Refused(RefusedReason.NotSigned)
            else -> ConnectionVerdict.Accepted(unencrypted = !info.encrypted)
        }
    }
}
