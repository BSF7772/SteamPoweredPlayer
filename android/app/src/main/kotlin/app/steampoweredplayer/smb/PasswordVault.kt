// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.smb

import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.io.File
import java.security.GeneralSecurityException
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

/**
 * Passwords per profile: AES-256-GCM under a non-exportable Android Keystore key, each sealed value in a private app file. A value that can't
 * be opened (a new device, a key the OS dropped) reads as none, so the app asks for the password again rather than failing.
 */
class PasswordVault(private val folder: File) {
    fun put(profileId: String, password: String) {
        folder.mkdirs()
        val cipher = Cipher.getInstance(TRANSFORMATION).apply { init(Cipher.ENCRYPT_MODE, key()) }
        val sealed = cipher.doFinal(password.toByteArray(Charsets.UTF_8))
        file(profileId).writeBytes(byteArrayOf(cipher.iv.size.toByte()) + cipher.iv + sealed)
    }

    fun get(profileId: String): String? {
        val data = file(profileId).takeIf { it.exists() }?.readBytes() ?: return null
        return try {
            val ivSize = data[0].toInt()
            val cipher = Cipher.getInstance(TRANSFORMATION)
            cipher.init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(TAG_BITS, data, 1, ivSize))
            cipher.doFinal(data, 1 + ivSize, data.size - 1 - ivSize).toString(Charsets.UTF_8)
        } catch (_: GeneralSecurityException) {
            null
        } catch (_: IndexOutOfBoundsException) {
            null
        }
    }

    fun remove(profileId: String) {
        file(profileId).delete()
    }

    /** Named by a hash-free id the app generated itself, never by anything the user typed. */
    private fun file(profileId: String) = File(folder, "$profileId.sealed")

    private fun key(): SecretKey {
        val store = KeyStore.getInstance(KEYSTORE).apply { load(null) }
        (store.getKey(ALIAS, null) as SecretKey?)?.let { return it }
        return KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, KEYSTORE).apply {
            init(
                KeyGenParameterSpec.Builder(ALIAS, KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
                    .setBlockModes(KeyProperties.BLOCK_MODE_GCM)
                    .setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE)
                    .setKeySize(256)
                    .build(),
            )
        }.generateKey()
    }

    private companion object {
        const val KEYSTORE = "AndroidKeyStore"
        const val ALIAS = "spp-server-passwords"
        const val TRANSFORMATION = "AES/GCM/NoPadding"
        const val TAG_BITS = 128
    }
}
