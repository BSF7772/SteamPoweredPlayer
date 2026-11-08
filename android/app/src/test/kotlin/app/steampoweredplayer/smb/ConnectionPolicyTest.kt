// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.smb

import org.junit.Assert.assertEquals
import org.junit.Test

class ConnectionPolicyTest {
    private val ok = ConnectionInfo("SMB311", encrypted = true, cipher = "AES-128-GCM", signingEnforced = true)

    @Test fun an_encrypted_3x_session_is_accepted() {
        listOf("SMB300", "SMB302", "SMB311").forEach {
            assertEquals(ConnectionVerdict.Accepted(unencrypted = false), ConnectionPolicy.evaluate(ok.copy(dialect = it), requireEncryption = true))
        }
    }

    @Test fun older_or_unknown_dialects_are_refused_even_with_the_override() {
        listOf("SMB1", "SMB202", "SMB210", "?").forEach {
            assertEquals(ConnectionVerdict.Refused(RefusedReason.OldDialect), ConnectionPolicy.evaluate(ok.copy(dialect = it), requireEncryption = false))
        }
    }

    @Test fun an_unencrypted_session_is_refused_by_default_never_downgraded() =
        assertEquals(ConnectionVerdict.Refused(RefusedReason.NotEncrypted), ConnectionPolicy.evaluate(ok.copy(encrypted = false, cipher = "none"), requireEncryption = true))

    @Test fun the_override_allows_it_and_says_so() =
        assertEquals(ConnectionVerdict.Accepted(unencrypted = true), ConnectionPolicy.evaluate(ok.copy(encrypted = false, cipher = "none"), requireEncryption = false))

    @Test fun neither_signed_nor_encrypted_is_refused_even_with_the_override() =
        assertEquals(ConnectionVerdict.Refused(RefusedReason.NotSigned), ConnectionPolicy.evaluate(ok.copy(encrypted = false, signingEnforced = false), requireEncryption = false))
}
