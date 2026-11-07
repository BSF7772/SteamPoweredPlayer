// SPDX-License-Identifier: GPL-3.0-or-later
package spp.spike.s3

import org.codelibs.jcifs.smb.CIFSContext
import org.codelibs.jcifs.smb.config.PropertyConfiguration
import org.codelibs.jcifs.smb.context.BaseContext
import org.codelibs.jcifs.smb.impl.NtlmPasswordAuthenticator
import org.codelibs.jcifs.smb.impl.SmbFile
import org.codelibs.jcifs.smb.internal.smb2.Smb2EncryptionContext
import org.json.JSONObject
import java.net.InetSocketAddress
import java.net.Socket
import java.util.Properties

/** One server profile (LAN or VPN). Host values come from the device settings, never from the repo. */
data class Profile(val name: String, val host: String, val share: String, val domain: String, val user: String, val password: String)

class ConnInfo(val dialect: String, val encrypted: Boolean, val cipher: String, val signingEnforced: Boolean) {
    fun json() = JSONObject().put("dialect", dialect).put("encrypted", encrypted).put("cipher", cipher).put("signing_enforced", signingEnforced)
}

object Smb {
    /** jcifs settings: SMB 3.0 .. 3.1.1 only, encryption allowed (jcifs encrypts when the server/share requires it), signing enforced. */
    fun context(p: Profile): CIFSContext {
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
        return BaseContext(PropertyConfiguration(props)).withCredentials(NtlmPasswordAuthenticator(p.domain, p.user, p.password))
    }

    fun url(p: Profile, path: String) = "smb://${p.host}/${p.share}/" + path.trimStart('/')

    /**
     * Negotiated dialect / cipher / signing. jcifs 3.0.4 has no public API for this and its impl classes are package-private,
     * so this reads them by reflection (pinned version; a product build would need an upstream accessor).
     */
    fun info(f: SmbFile): ConnInfo {
        val th = f.treeHandle
        try {
            val s = call(th, "getSession")!!
            try {
                val ec = call(s, "getEncryptionContext") as Smb2EncryptionContext?
                val cipher = when (ec?.cipherId) {
                    null -> "none"
                    Smb2EncryptionContext.CIPHER_AES_128_CCM -> "AES-128-CCM"
                    Smb2EncryptionContext.CIPHER_AES_128_GCM -> "AES-128-GCM"
                    Smb2EncryptionContext.CIPHER_AES_256_CCM -> "AES-256-CCM"
                    Smb2EncryptionContext.CIPHER_AES_256_GCM -> "AES-256-GCM"
                    else -> "id ${ec.cipherId}"
                }
                val t = call(s, "getTransport")!!
                val nego = call(t, "getNegotiateResponse")
                val dialect = (nego?.let { call(it, "getSelectedDialect") } ?: ec?.dialect)?.toString() ?: "?"
                val enc = call(s, "isEncryptionEnabled") as Boolean
                val sign = call(t, "isSigningEnforced") as Boolean
                return ConnInfo(dialect, enc, cipher, sign)
            } finally { (s as? AutoCloseable)?.close() }
        } finally { th.close() }
    }

    private fun call(o: Any, name: String): Any? {
        var c: Class<*>? = o.javaClass
        while (c != null) {
            try { return c.getDeclaredMethod(name).apply { isAccessible = true }.invoke(o) } catch (_: NoSuchMethodException) {}
            c = c.superclass
        }
        error("no method $name on ${o.javaClass.name}")
    }

    /** Cheap reachability probe: TCP connect to :445. */
    fun reachable(host: String, timeoutMs: Int = 1500): Boolean = try {
        Socket().use { it.connect(InetSocketAddress(host, 445), timeoutMs) }; true
    } catch (e: Exception) { false }
}
