// SPDX-License-Identifier: GPL-3.0-or-later
// Spike S3 driver. Setup is done ON THE PHONE (the password is typed there and stored encrypted with an Android Keystore key);
// hosts/share/user may also be set over adb. Runs are started over adb, results go to files/results/<tag>.json (tag SPP-S3):
//   am start -n spp.spike.s3/.MainActivity --es mode config --es lan <host> --es vpn <host> --es share <share> --es user <u> [--es domain <d>]
//   am start -n spp.spike.s3/.MainActivity --es mode probe                       (connect each profile, report dialect/cipher)
//   am start -n spp.spike.s3/.MainActivity --es mode run --es session video/bg_... --ei first 1 --ei count 100
//        [--ei parallel 2] [--es tag run1] [--es only lan|vpn] [--ei vpn_max_mb 50] [--ez require_enc true] [--ez fresh false]
package spp.spike.s3

import android.app.Activity
import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import android.net.NetworkRequest
import android.os.Build
import android.os.Bundle
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import android.text.InputType
import android.util.Base64
import android.util.Log
import android.view.WindowManager
import android.widget.Button
import android.widget.EditText
import android.widget.LinearLayout
import android.widget.ScrollView
import android.widget.TextView
import org.codelibs.jcifs.smb.impl.SmbFile
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.security.KeyStore
import javax.crypto.Cipher
import javax.crypto.KeyGenerator
import javax.crypto.SecretKey
import javax.crypto.spec.GCMParameterSpec

class MainActivity : Activity() {
    private lateinit var log: TextView
    private lateinit var files: File
    private val prefs by lazy { getSharedPreferences("cfg", Context.MODE_PRIVATE) }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        window.addFlags(WindowManager.LayoutParams.FLAG_KEEP_SCREEN_ON)
        files = getExternalFilesDir(null)!!
        val root = LinearLayout(this).apply { orientation = LinearLayout.VERTICAL; setPadding(24, 24, 24, 24) }
        fun field(key: String, hint: String, secret: Boolean = false) = EditText(this).apply {
            this.hint = hint
            if (secret) inputType = InputType.TYPE_CLASS_TEXT or InputType.TYPE_TEXT_VARIATION_PASSWORD
            else setText(prefs.getString(key, ""))
            root.addView(this)
        }
        val lan = field("lan", "LAN host (IP or name)")
        val vpn = field("vpn", "VPN host (IP or name)")
        val share = field("share", "share")
        val domain = field("domain", "domain (optional)")
        val user = field("user", "user")
        val pass = field("", if (prefs.contains("pw")) "password (saved; type to replace)" else "password", secret = true)
        root.addView(Button(this).apply {
            text = "Save"
            setOnClickListener {
                val e = prefs.edit().putString("lan", lan.text.toString().trim()).putString("vpn", vpn.text.toString().trim())
                    .putString("share", share.text.toString().trim()).putString("domain", domain.text.toString().trim())
                    .putString("user", user.text.toString().trim())
                if (pass.text.isNotEmpty()) e.putString("pw", encrypt(pass.text.toString()))
                e.apply(); pass.setText(""); say("saved (password ${if (prefs.contains("pw")) "stored, Keystore-encrypted" else "missing"})")
            }
        })
        log = TextView(this).apply { textSize = 11f }
        root.addView(log)
        setContentView(ScrollView(this).apply { addView(root) })
        say("S3 SMB download spike, ${Build.MANUFACTURER} ${Build.MODEL}, SDK ${Build.VERSION.SDK_INT}")

        when (intent.getStringExtra("mode")) {
            "config" -> {
                val e = prefs.edit()
                for (k in listOf("lan", "vpn", "share", "user", "domain")) intent.getStringExtra(k)?.let { e.putString(k, it) }
                e.commit()
                lan.setText(prefs.getString("lan", "")); vpn.setText(prefs.getString("vpn", "")); share.setText(prefs.getString("share", ""))
                domain.setText(prefs.getString("domain", "")); user.setText(prefs.getString("user", ""))
                say("config stored; the password must be typed on the phone")
            }
            "probe" -> Thread { guard("probe") { probe() } }.start()
            "run" -> Thread { guard("run") { run() } }.start()
        }
    }

    private fun profiles(): List<Profile> {
        val pw = prefs.getString("pw", null)?.let { decrypt(it) } ?: error("no password saved: type it on the phone and press Save")
        val share = prefs.getString("share", "")!!
        val d = prefs.getString("domain", "")!!
        val u = prefs.getString("user", "")!!
        return listOf("lan", "vpn").mapNotNull { k ->
            prefs.getString(k, "")!!.takeIf { it.isNotEmpty() }?.let { Profile(k, it, share, d, u, pw) }
        }
    }

    private fun probe() {
        val out = JSONArray()
        for (p in profiles()) {
            val o = JSONObject().put("profile", p.name)
            val t0 = System.nanoTime()
            o.put("tcp445", Smb.reachable(p.host, 3000))
            try {
                val c = Smb.context(p)
                val root = SmbFile(Smb.url(p, ""), c)
                val names = root.listFiles().map { it.name }
                o.put("info", Smb.info(root).json()).put("top_level_entries", names.size)
                c.close()
            } catch (t: Throwable) { o.put("error", t.toString()); Log.e(TAG, "probe", t) }
            o.put("ms", (System.nanoTime() - t0) / 1_000_000)
            out.put(o); say("probe ${o}")
        }
        write("probe", JSONObject().put("mode", "probe").put("profiles", out))
    }

    private fun run() {
        val session = intent.getStringExtra("session") ?: error("--es session video/bg_...")
        val first = intent.getIntExtra("first", 1)
        val count = intent.getIntExtra("count", 100)
        val tag = intent.getStringExtra("tag") ?: "run"
        val only = intent.getStringExtra("only")
        val all = profiles()
        val ps = if (only != null) all.filter { it.name == only } else all
        val cache = File(files, "cache/" + session.substringAfterLast('/'))
        if (intent.getBooleanExtra("fresh", false)) cache.listFiles()?.forEach { it.delete() }   // app-private cache only
        val dl = Downloader(ps, session, cache, intent.getIntExtra("parallel", 2), intent.getBooleanExtra("require_enc", true),
            intent.getIntExtra("vpn_max_mb", 50) * 1_000_000L) { say(it) }
        val cm = getSystemService(ConnectivityManager::class.java)
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = dl.onNetworkChange("available $network")
            override fun onLost(network: Network) = dl.onNetworkChange("lost $network")
            override fun onCapabilitiesChanged(network: Network, nc: NetworkCapabilities) {
                val t = listOfNotNull(
                    "wifi".takeIf { nc.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) },
                    "cell".takeIf { nc.hasTransport(NetworkCapabilities.TRANSPORT_CELLULAR) },
                    "eth".takeIf { nc.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET) },
                    "vpn".takeIf { nc.hasTransport(NetworkCapabilities.TRANSPORT_VPN) })
                dl.event("caps", "$network ${t.joinToString("+")}")
            }
        }
        // With a VPN up the default network IS the VPN and survives Wi-Fi coming and going (only its caps change), so the default
        // callback never reports the change. A request without TRANSPORT_VPN (the builder's default NOT_VPN) sees the real networks.
        // VALIDATED is a second trigger: at onAvailable the route to the LAN may not be usable yet.
        val validated = java.util.concurrent.ConcurrentHashMap<Network, Boolean>()
        val real = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = dl.onNetworkChange("real available $network")
            override fun onLost(network: Network) { validated.remove(network); dl.onNetworkChange("real lost $network") }
            override fun onCapabilitiesChanged(network: Network, nc: NetworkCapabilities) {
                val v = nc.hasCapability(NetworkCapabilities.NET_CAPABILITY_VALIDATED)
                if (validated.put(network, v) != v && v) dl.onNetworkChange("real validated $network")
            }
        }
        cm.registerDefaultNetworkCallback(cb)
        cm.registerNetworkCallback(NetworkRequest.Builder().addCapability(NetworkCapabilities.NET_CAPABILITY_INTERNET).build(), real)
        try {
            val items = dl.plan(first, count)
            val r = dl.run(items)
            write(tag, r.put("mode", "run").put("session_name_hash", session.hashCode()).put("first", first).put("count", count)
                .put("profiles", JSONArray(ps.map { it.name })))
        } finally { cm.unregisterNetworkCallback(cb); cm.unregisterNetworkCallback(real) }
    }

    private fun guard(what: String, f: () -> Unit) = try { f() } catch (t: Throwable) {
        say("ERROR $what: $t"); Log.e(TAG, what, t)
        write("error-$what", JSONObject().put("mode", what).put("error", t.toString()))
    }

    private fun write(name: String, o: JSONObject) {
        o.put("device", "${Build.MANUFACTURER} ${Build.MODEL} SDK ${Build.VERSION.SDK_INT}")
        val dir = File(files, "results").apply { mkdirs() }
        File(dir, "$name.json").writeText(o.toString(2))
        say("DONE results/$name.json")
    }

    // --- password at rest: AES-256-GCM with a non-exportable Android Keystore key
    private fun key(): SecretKey {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        (ks.getKey("smb-pw", null) as? SecretKey)?.let { return it }
        val g = KeyGenerator.getInstance(KeyProperties.KEY_ALGORITHM_AES, "AndroidKeyStore")
        g.init(KeyGenParameterSpec.Builder("smb-pw", KeyProperties.PURPOSE_ENCRYPT or KeyProperties.PURPOSE_DECRYPT)
            .setBlockModes(KeyProperties.BLOCK_MODE_GCM).setEncryptionPaddings(KeyProperties.ENCRYPTION_PADDING_NONE).setKeySize(256).build())
        return g.generateKey()
    }
    private fun encrypt(s: String): String {
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.ENCRYPT_MODE, key()) }
        val ct = c.doFinal(s.toByteArray())
        return Base64.encodeToString(c.iv, Base64.NO_WRAP) + ":" + Base64.encodeToString(ct, Base64.NO_WRAP)
    }
    private fun decrypt(s: String): String {
        val (iv, ct) = s.split(':').map { Base64.decode(it, Base64.NO_WRAP) }
        val c = Cipher.getInstance("AES/GCM/NoPadding").apply { init(Cipher.DECRYPT_MODE, key(), GCMParameterSpec(128, iv)) }
        return String(c.doFinal(ct))
    }

    private fun say(s: String) {
        Log.i(TAG, s)
        runOnUiThread { log.append(s + "\n") }
    }

    companion object { const val TAG = "SPP-S3" }
}
