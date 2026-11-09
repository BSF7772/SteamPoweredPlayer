// SPDX-License-Identifier: GPL-3.0-or-later
package app.steampoweredplayer.smb

import app.steampoweredplayer.data.ServerProfile
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import java.util.Properties
import org.codelibs.jcifs.smb.CIFSContext
import org.codelibs.jcifs.smb.CIFSException
import org.codelibs.jcifs.smb.config.PropertyConfiguration
import org.codelibs.jcifs.smb.context.BaseContext
import org.codelibs.jcifs.smb.impl.NtlmPasswordAuthenticator
import org.codelibs.jcifs.smb.impl.SmbFile
import org.codelibs.jcifs.smb.internal.smb2.Smb2EncryptionContext

/** What trying a profile came to. Messages are for the user and hold no password. */
sealed interface ConnectionResult {
    data class Connected(val info: ConnectionInfo, val verdict: ConnectionVerdict.Accepted) : ConnectionResult
    data class Refused(val info: ConnectionInfo, val reason: RefusedReason) : ConnectionResult
    data object Unreachable : ConnectionResult
    data class Failed(val message: String) : ConnectionResult
}

/** JCIFS with the settings the spike validated on a real phone (S3). Read-only: nothing here writes to the share. Blocking: call off the main thread. */
object SmbClient {
    private const val PROBE_TIMEOUT_MS = 1500

    fun context(profile: ServerProfile, password: String): CIFSContext {
        val props = Properties().apply {
            setProperty("jcifs.client.minVersion", "SMB300")
            setProperty("jcifs.client.maxVersion", "SMB311")
            setProperty("jcifs.client.useSMB2Negotiation", "true")
            setProperty("jcifs.client.encryptionEnabled", "true")
            setProperty("jcifs.client.signingEnforced", "true")
            setProperty("jcifs.client.connTimeout", "4000")
            setProperty("jcifs.client.responseTimeout", "15000")
            setProperty("jcifs.client.soTimeout", "20000")
            setProperty("jcifs.client.maxRequestRetries", "1")
        }
        return BaseContext(PropertyConfiguration(props)).withCredentials(NtlmPasswordAuthenticator(profile.domain, profile.user, password))
    }

    /** A cheap reachability probe: a TCP connect to the SMB port. */
    fun reachable(profile: ServerProfile, timeoutMs: Int = PROBE_TIMEOUT_MS): Boolean = try {
        Socket().use { it.connect(InetSocketAddress(profile.host, profile.port), timeoutMs) }
        true
    } catch (_: IOException) {
        false
    }

    /** Connects and lists the share's root, then holds the session against the policy, so an unencrypted one never gets used by accident. */
    fun tryConnect(profile: ServerProfile, password: String): ConnectionResult {
        if (!reachable(profile)) return ConnectionResult.Unreachable
        return try {
            SmbFile(profile.url(), context(profile, password)).use { root ->
                root.exists()
                val info = info(root)
                when (val verdict = ConnectionPolicy.evaluate(info, profile.requireEncryption)) {
                    is ConnectionVerdict.Accepted -> ConnectionResult.Connected(info, verdict)
                    is ConnectionVerdict.Refused -> ConnectionResult.Refused(info, verdict.reason)
                }
            }
        } catch (e: CIFSException) {
            ConnectionResult.Failed(e.message ?: e.javaClass.simpleName)
        } catch (e: IOException) {
            ConnectionResult.Failed(e.message ?: e.javaClass.simpleName)
        }
    }

    /** The server's share names, as the signed-in user may see them. Needs only the host and the credentials. */
    @Throws(IOException::class)
    fun shares(profile: ServerProfile, password: String): List<String> = try {
        val host = if (profile.host.contains(':') && !profile.host.startsWith("[")) "[${profile.host}]" else profile.host
        val port = if (profile.port == ServerProfile.DEFAULT_PORT) "" else ":${profile.port}"
        SmbFile("smb://$host$port/", context(profile, password)).use { server -> server.list().toList() }
    } catch (e: CIFSException) {
        throw IOException(e.message ?: e.javaClass.simpleName, e)
    }

    /**
     * Negotiated dialect, cipher and signing. jcifs 3.0.4 has no public API for them and its impl classes are package-private, so this uses
     * reflection, which the pinned version keeps stable. Replace it when upstream has an accessor.
     */
    private fun info(file: SmbFile): ConnectionInfo = file.treeHandle.use { tree ->
        val session = call(tree, "getSession")!!
        try {
            val encryption = call(session, "getEncryptionContext") as Smb2EncryptionContext?
            val transport = call(session, "getTransport")!!
            val negotiated = call(transport, "getNegotiateResponse")
            ConnectionInfo(
                dialect = (negotiated?.let { call(it, "getSelectedDialect") } ?: encryption?.dialect)?.toString() ?: "?",
                encrypted = call(session, "isEncryptionEnabled") as Boolean,
                cipher = cipherName(encryption?.cipherId),
                signingEnforced = call(transport, "isSigningEnforced") as Boolean,
            )
        } finally {
            (session as? AutoCloseable)?.close()
        }
    }

    private fun cipherName(id: Int?): String = when (id) {
        null -> "none"
        Smb2EncryptionContext.CIPHER_AES_128_CCM -> "AES-128-CCM"
        Smb2EncryptionContext.CIPHER_AES_128_GCM -> "AES-128-GCM"
        Smb2EncryptionContext.CIPHER_AES_256_CCM -> "AES-256-CCM"
        Smb2EncryptionContext.CIPHER_AES_256_GCM -> "AES-256-GCM"
        else -> "id $id"
    }

    private fun call(target: Any, name: String): Any? {
        var c: Class<*>? = target.javaClass
        while (c != null) {
            try {
                return c.getDeclaredMethod(name).apply { isAccessible = true }.invoke(target)
            } catch (_: NoSuchMethodException) {
                c = c.superclass
            }
        }
        error("no method $name on ${target.javaClass.name}")
    }
}
