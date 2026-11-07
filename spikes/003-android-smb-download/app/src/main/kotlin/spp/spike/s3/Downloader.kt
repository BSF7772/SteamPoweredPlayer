// SPDX-License-Identifier: GPL-3.0-or-later
// Download-first chunk fetcher (plan 4.3): the unit is Steam's own chunk file; each is fetched to <name>.part, checked by size,
// then renamed. A dropped connection loses at most the bytes in flight: a retry resumes the .part at its current length.
// Priority: init segments, then chunks from the playhead forward. Profiles are tried in order (LAN before VPN) with a short TCP
// probe; network changes (ConnectivityManager callback) wake the retry wait instead of polling.
package spp.spike.s3

import org.codelibs.jcifs.smb.CIFSContext
import org.codelibs.jcifs.smb.impl.SmbFile
import org.codelibs.jcifs.smb.impl.SmbFileInputStream
import org.json.JSONArray
import org.json.JSONObject
import java.io.File
import java.io.FileOutputStream
import java.security.MessageDigest
import java.util.concurrent.ConcurrentLinkedQueue
import java.util.concurrent.PriorityBlockingQueue
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import java.util.concurrent.atomic.AtomicLong
import java.util.concurrent.locks.ReentrantLock
import kotlin.concurrent.withLock
import kotlin.random.Random

class Downloader(
    private val profiles: List<Profile>,
    private val sessionPath: String,          // e.g. video/bg_<appid>_<date>_<time>
    private val cacheDir: File,
    private val parallel: Int,
    private val requireEncryption: Boolean,
    private val vpnMaxBytes: Long,            // cap on bytes fetched through non-first profiles (mobile data guard)
    private val say: (String) -> Unit,
) {
    class Item(val name: String, val size: Long, val prio: Int) : Comparable<Item> {
        @Volatile var attempts = 0
        @Volatile var fetched = 0L             // bytes read from the network for this file (incl. re-reads)
        @Volatile var resumedAt = ArrayList<Long>()
        @Volatile var profile = ""
        @Volatile var sha256 = ""
        @Volatile var ms = 0L
        @Volatile var done = false
        override fun compareTo(other: Item) = prio.compareTo(other.prio)
    }

    private val events = ConcurrentLinkedQueue<JSONObject>()
    private val t0 = System.nanoTime()
    private fun now() = (System.nanoTime() - t0) / 1_000_000
    fun event(kind: String, detail: String) {
        events.add(JSONObject().put("t_ms", now()).put("kind", kind).put("detail", detail)); say("[$kind] $detail")
    }

    // --- active connection (shared by workers, replaced on failure)
    private val lock = ReentrantLock()
    private val netChanged = lock.newCondition()
    @Volatile private var ctx: CIFSContext? = null
    @Volatile private var active: Profile? = null
    @Volatile private var generation = 0
    val connInfos = JSONArray()
    private val viaFallback = AtomicLong()

    /**
     * Network changed: wake waiting retries, and fail back to the first (LAN) profile when it is reachable again while a
     * fallback profile is active. Dropping the fallback session makes in-flight reads fail; they resume their .part on LAN.
     * Event-driven only (ConnectivityManager callbacks), no polling.
     */
    fun onNetworkChange(what: String) {
        event("net", what)
        lock.withLock { netChanged.signalAll() }
        val a = active ?: return
        if (a === profiles.first()) return
        val gen = generation
        Thread {
            if (Smb.reachable(profiles.first().host)) { event("failback", "${profiles.first().name} reachable again"); invalidate(gen) }
        }.start()
    }

    private class BudgetExceeded : java.io.IOException("fallback byte budget used up")

    private fun connect(): Pair<Profile, CIFSContext> {
        lock.withLock {
            ctx?.let { c -> return active!! to c }
            for (p in profiles) {
                if (p !== profiles.first() && viaFallback.get() >= vpnMaxBytes) { event("skip", "${p.name}: fallback byte budget used up"); continue }
                if (!Smb.reachable(p.host)) { event("probe", "${p.name} unreachable"); continue }
                try {
                    val c = Smb.context(p)
                    val dir = SmbFile(Smb.url(p, sessionPath) + "/", c)
                    val info = Smb.info(dir)
                    connInfos.put(info.json().put("profile", p.name).put("t_ms", now()))
                    event("connect", "${p.name}: ${info.dialect} encrypted=${info.encrypted} ${info.cipher} signing=${info.signingEnforced}")
                    if (requireEncryption && !info.encrypted) {
                        c.close(); throw IllegalStateException("${p.name}: session is not encrypted, refusing (require encryption is on)")
                    }
                    ctx = c; active = p; generation++
                    return p to c
                } catch (e: IllegalStateException) { throw e }
                catch (e: Exception) { event("connect-fail", "${p.name}: $e") }
            }
            throw java.io.IOException("no profile reachable")
        }
    }

    private fun invalidate(gen: Int) {
        lock.withLock {
            if (gen == generation && ctx != null) { try { ctx!!.close() } catch (_: Exception) {}; ctx = null; active = null; event("reset", "connection dropped") }
        }
    }

    /** Lists the session folder and returns init + chunk items for [first, first+count). */
    fun plan(first: Int, count: Int): List<Item> {
        val (p, c) = connect()
        val dir = SmbFile(Smb.url(p, sessionPath) + "/", c)
        val all = dir.listFiles().associate { it.name to it.length() }
        val items = ArrayList<Item>()
        var prio = 0
        for (n in listOf("init-stream0.m4s", "init-stream1.m4s")) all[n]?.let { items.add(Item(n, it, prio++)) }
        for (i in first until first + count) for (s in 0..1) {
            val n = "chunk-stream$s-%05d.m4s".format(i)
            all[n]?.let { items.add(Item(n, it, prio++)) }
        }
        event("plan", "${all.size} files in session, ${items.size} selected (${items.sumOf { it.size } / 1e6} MB)")
        return items
    }

    fun run(items: List<Item>): JSONObject {
        cacheDir.mkdirs()
        val queue = PriorityBlockingQueue(items.filter { !alreadyDone(it) })
        val skipped = items.size - queue.size
        val failedHard = AtomicInteger()
        val bytes = AtomicLong()
        val start = now()
        val workers = (0 until parallel).map { w ->
            Thread {
                while (true) {
                    val it = queue.poll() ?: break
                    if (!fetch(it, bytes)) { failedHard.incrementAndGet() }
                }
            }.apply { name = "dl-$w"; start() }
        }
        workers.forEach { it.join() }
        val wall = now() - start
        val arr = JSONArray()
        var refetched = 0L
        for (i in items) {
            val extra = maxOf(0L, i.fetched - (if (i.done) i.size else 0))
            refetched += extra
            arr.put(JSONObject().put("name", i.name).put("size", i.size).put("done", i.done).put("attempts", i.attempts)
                .put("fetched", i.fetched).put("refetched", extra).put("resumed_at", JSONArray(i.resumedAt)).put("profile", i.profile)
                .put("sha256", i.sha256).put("ms", i.ms))
        }
        return JSONObject().put("files", items.size).put("skipped_existing", skipped).put("failed", failedHard.get())
            .put("bytes", bytes.get()).put("refetched_bytes", refetched).put("wall_ms", wall)
            .put("mb_per_s", if (wall > 0) bytes.get() / 1e3 / wall else 0).put("via_fallback_bytes", viaFallback.get())
            .put("connections", connInfos).put("events", JSONArray(events.toList())).put("items", arr)
    }

    private fun alreadyDone(i: Item): Boolean {
        val f = File(cacheDir, i.name)
        if (f.exists() && f.length() == i.size) { i.done = true; i.sha256 = sha(f); return true }
        return false
    }

    private fun sha(f: File): String {
        val md = MessageDigest.getInstance("SHA-256")
        f.inputStream().use { s -> val b = ByteArray(1 shl 16); while (true) { val n = s.read(b); if (n < 0) break; md.update(b, 0, n) } }
        return md.digest().joinToString("") { "%02x".format(it) }
    }

    /** One file with retries: exponential backoff 0.5 s .. 8 s with jitter, woken early by a network change. */
    private fun fetch(i: Item, total: AtomicLong): Boolean {
        val part = File(cacheDir, i.name + ".part")
        val fin = File(cacheDir, i.name)
        val ts = System.nanoTime()
        var backoff = 500L
        while (i.attempts < 30) {
            i.attempts++
            var gen = -1
            try {
                val (p, c) = connect()
                gen = generation
                i.profile = p.name
                val off = if (part.exists()) part.length() else 0L
                if (off > 0) i.resumedAt.add(off)
                if (off > i.size) part.delete()
                val f = SmbFile(Smb.url(p, sessionPath) + "/" + i.name, c)
                // read-only open that lets Steam keep writing/trimming: FILE_SHARE_READ|WRITE|DELETE (= 7)
                f.openInputStream(7).use { input: SmbFileInputStream ->
                    if (off > 0) { var s = off; while (s > 0) { val k = input.skip(s); if (k <= 0) break; s -= k } }
                    FileOutputStream(part, off > 0).use { out ->
                        val b = ByteArray(256 * 1024)
                        while (true) {
                            val n = input.read(b)
                            if (n < 0) break
                            out.write(b, 0, n)
                            i.fetched += n; total.addAndGet(n.toLong())
                            // the fallback budget is enforced per read, not only at connect: a VPN over mobile data must stop
                            if (p !== profiles.first() && viaFallback.addAndGet(n.toLong()) >= vpnMaxBytes) throw BudgetExceeded()
                        }
                        out.fd.sync()
                    }
                }
                if (part.length() != i.size) throw java.io.IOException("size ${part.length()} != ${i.size}")
                if (!part.renameTo(fin)) throw java.io.IOException("rename failed")
                i.sha256 = sha(fin); i.done = true; i.ms = (System.nanoTime() - ts) / 1_000_000
                return true
            } catch (e: IllegalStateException) {
                event("refuse", e.message ?: e.toString()); return false
            } catch (e: BudgetExceeded) {
                event("budget", "${i.name}: ${e.message}; waiting for the first profile")
                invalidate(gen)
                lock.withLock { netChanged.await(30, TimeUnit.SECONDS) }   // woken by a network change
            } catch (e: Exception) {
                event("retry", "${i.name} attempt ${i.attempts}: ${e.javaClass.simpleName}: ${e.message}")
                if (gen >= 0) invalidate(gen)
                val wait = backoff + Random.nextLong(backoff / 2 + 1)
                lock.withLock { netChanged.await(wait, TimeUnit.MILLISECONDS) }
                backoff = minOf(8000L, backoff * 2)
            }
        }
        i.ms = (System.nanoTime() - ts) / 1_000_000
        return false
    }
}
