// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.smb

import androidx.test.ext.junit.runners.AndroidJUnit4
import androidx.test.platform.app.InstrumentationRegistry
import java.io.File
import java.util.UUID
import org.junit.After
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Before
import org.junit.Test
import org.junit.runner.RunWith

/** Against the real Android Keystore. On an emulator that is the software-backed one; hardware backing needs a real phone. */
@RunWith(AndroidJUnit4::class)
class PasswordVaultTest {
    private lateinit var folder: File
    private lateinit var vault: PasswordVault

    @Before
    fun setUp() {
        val context = InstrumentationRegistry.getInstrumentation().targetContext
        folder = File(context.cacheDir, "vault-test-${UUID.randomUUID()}")
        vault = PasswordVault(folder)
    }

    @After
    fun tearDown() {
        folder.deleteRecursively()
    }

    @Test
    fun aPasswordComesBackAsItWasStored() {
        vault.put("a", "correct horse ünï çødé 🔑")
        assertEquals("correct horse ünï çødé 🔑", vault.get("a"))
    }

    @Test
    fun aNewVaultOnTheSameFolderReadsIt() {
        vault.put("a", "secret")
        assertEquals("secret", PasswordVault(folder).get("a"))
    }

    @Test
    fun theFileNeverHoldsThePassword() {
        vault.put("a", "plainly-visible-secret")
        val onDisk = File(folder, "a.sealed").readBytes().toString(Charsets.ISO_8859_1)
        assertFalse(onDisk.contains("plainly-visible-secret"))
    }

    @Test
    fun storingAgainReplacesAndProfilesStayApart() {
        vault.put("a", "one")
        vault.put("b", "two")
        vault.put("a", "three")
        assertEquals("three", vault.get("a"))
        assertEquals("two", vault.get("b"))
    }

    @Test
    fun removedOrUnknownReadsAsNone() {
        vault.put("a", "secret")
        vault.remove("a")
        assertNull(vault.get("a"))
        assertNull(vault.get("never-stored"))
    }

    @Test
    fun aTamperedValueReadsAsNone() {
        vault.put("a", "secret")
        val file = File(folder, "a.sealed")
        val bytes = file.readBytes()
        bytes[bytes.size - 1] = (bytes[bytes.size - 1].toInt() xor 1).toByte()
        file.writeBytes(bytes)
        assertNull(vault.get("a"))
    }

    @Test
    fun aDamagedFileReadsAsNoneInsteadOfThrowing() {
        folder.mkdirs()
        val file = File(folder, "a.sealed")
        val damaged = listOf(
            byteArrayOf(),                  // empty
            byteArrayOf(-1, 1, 2, 3),       // a negative iv length
            byteArrayOf(100, 1, 2, 3),      // an iv longer than the file
            byteArrayOf(12, 1, 2, 3),       // too short for its iv
            byteArrayOf(0, 1, 2, 3),        // no iv at all
        )
        for (bytes in damaged) {
            file.writeBytes(bytes)
            assertNull("bytes ${bytes.toList()}", vault.get("a"))
        }
    }
}
