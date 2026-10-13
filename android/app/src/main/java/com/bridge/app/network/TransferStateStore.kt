package com.bridge.app.network

import com.bridge.app.protocol.BridgeJson
import java.io.File
import kotlinx.serialization.Serializable

/**
 * Small persistent state that makes transfers survive disconnects and app
 * restarts, mirroring Bridge.Network.TransferStateStore on Windows:
 *  - outbox: files we're sending that the PC hasn't acknowledged yet;
 *  - completed: transfer ids received recently, so a re-offer after a lost ack
 *    is answered "ok" instead of storing the file twice;
 *  - batches: which top folder name a folder transfer was given.
 * Entries expire after 24 h. With no [directory] it is in-memory only.
 */
class TransferStateStore(directory: File? = null, private val now: () -> Long = System::currentTimeMillis) : java.io.Closeable {
    @Serializable
    data class OutboxEntry(
        val transferId: String,
        val peerId: String,
        val filePath: String,
        val fileName: String,
        val relativePath: String? = null,
        val batchId: String? = null,
        val batchIndex: Int? = null,
        val batchCount: Int? = null,
        val created: Long,
        /** A private copy (e.g. of a shared content URI) to delete once the send is finished. */
        val deleteWhenDone: Boolean = false,
        /** Cached hash, valid while [size] and [lastModified] still match the file. */
        val sha256: String? = null,
        val size: Long? = null,
        val lastModified: Long? = null,
    )

    /** A stored file; [sha256] so a reused id with other content isn't mistaken for it. */
    @Serializable data class CompletedEntry(val key: String, val savedPath: String, val at: Long, val sha256: String? = null)
    @Serializable data class BatchRoot(val key: String, val rootName: String, val at: Long)
    @Serializable private data class State(
        val outbox: MutableList<OutboxEntry> = mutableListOf(),
        val completed: MutableList<CompletedEntry> = mutableListOf(),
        val batches: MutableList<BatchRoot> = mutableListOf(),
    )

    companion object {
        const val LIFETIME_MS = 24L * 60 * 60 * 1000
        const val MAX_COMPLETED = 500
        /** Changes are written at most this often (a big folder changes state once per file). */
        const val FLUSH_DELAY_MS = 250L
        fun key(peerId: String, id: String) = "$peerId|$id"
    }

    private val file = directory?.let { File(it, "transfer-state.json") }
    // Append-only journals: written at once, so they survive Android killing the process.
    private val completedLog = directory?.let { File(it, "completed.log") }
    private val batchLog = directory?.let { File(it, "batches.log") }
    private val lock = Any()
    private val state: State = load()
    // Declared before init: prune() may call save().
    private val flusher = java.util.concurrent.Executors.newSingleThreadScheduledExecutor { r -> Thread(r, "bridge-state").apply { isDaemon = true } }
    private var dirty = false
    private var flushScheduled = false

    init {
        synchronized(lock) {
            state.completed.clear(); state.completed += readJournal(completedLog, CompletedEntry.serializer())
            state.batches.clear(); state.batches += readJournal(batchLog, BatchRoot.serializer())
            prune()
        }
    }

    fun pendingFor(peerId: String): List<OutboxEntry> = synchronized(lock) { prune(); state.outbox.filter { it.peerId == peerId } }

    fun addOutbox(entry: OutboxEntry) = synchronized(lock) {
        state.outbox.removeAll { it.transferId == entry.transferId }
        state.outbox += entry
        save()
    }

    /** Queues several entries with one write (the rest of an interrupted folder). */
    fun addOutboxAll(entries: List<OutboxEntry>) = synchronized(lock) {
        val ids = entries.map { it.transferId }.toSet()
        state.outbox.removeAll { it.transferId in ids }
        state.outbox += entries
        save()
    }

    fun removeOutbox(transferId: String) = synchronized(lock) {
        val removed = state.outbox.filter { it.transferId == transferId }
        if (removed.isNotEmpty()) {
            state.outbox.removeAll(removed)
            save()
            removed.filter { it.deleteWhenDone }.forEach { File(it.filePath).delete() }
        }
    }

    /** Where this exact file (same id and hash) was stored, if we received it recently. */
    fun completedPath(peerId: String, transferId: String, sha256: String): String? = synchronized(lock) {
        state.completed.lastOrNull { it.key == key(peerId, transferId) && it.sha256.equals(sha256, ignoreCase = true) }?.savedPath
    }

    fun markCompleted(peerId: String, transferId: String, savedPath: String, sha256: String) = synchronized(lock) {
        val entry = CompletedEntry(key(peerId, transferId), savedPath, now(), sha256)
        state.completed += entry
        while (state.completed.size > MAX_COMPLETED) state.completed.removeAt(0)
        append(completedLog, entry, state.completed, CompletedEntry.serializer())
    }

    /** The root folder name for a batch, chosen with [choose] on first use and remembered. */
    fun batchRootName(peerId: String, batchId: String, choose: () -> String): String = synchronized(lock) {
        val k = key(peerId, batchId)
        state.batches.firstOrNull { it.key == k }?.rootName ?: choose().also {
            val entry = BatchRoot(k, it, now())
            state.batches += entry
            append(batchLog, entry, state.batches, BatchRoot.serializer())
        }
    }

    private fun prune() {
        val cutoff = now() - LIFETIME_MS
        val expired = state.outbox.filter { it.created < cutoff }
        state.outbox.removeAll(expired)
        expired.filter { it.deleteWhenDone }.forEach { File(it.filePath).delete() }
        if (expired.isNotEmpty()) save()
        state.completed.removeAll { it.at < cutoff } // journals are compacted as they grow
        state.batches.removeAll { it.at < cutoff }
    }

    /** Appends one JSON line; rewrites the journal from [live] when it gets long. */
    private fun <T> append(f: File?, entry: T, live: List<T>, serializer: kotlinx.serialization.KSerializer<T>) {
        f ?: return
        runCatching {
            f.parentFile?.mkdirs()
            f.appendText(BridgeJson.encodeToString(serializer, entry) + "\n")
            if (f.length() > 400L * MAX_COMPLETED) { // ~2× the live entries: compact
                val tmp = File(f.path + ".tmp")
                tmp.writeText(live.joinToString("") { BridgeJson.encodeToString(serializer, it) + "\n" })
                if (!tmp.renameTo(f)) { f.delete(); tmp.renameTo(f) }
            }
        }
    }

    private fun <T> readJournal(f: File?, serializer: kotlinx.serialization.KSerializer<T>): List<T> {
        if (f == null || !f.exists()) return emptyList()
        val list = runCatching { f.readLines() }.getOrDefault(emptyList())
            .filter { it.isNotBlank() }
            .mapNotNull { runCatching { BridgeJson.decodeFromString(serializer, it) }.getOrNull() } // a torn last line after a crash: skipped
        return list.takeLast(MAX_COMPLETED)
    }

    private fun load(): State {
        val f = file ?: return State()
        return runCatching { BridgeJson.decodeFromString(State.serializer(), f.readText()) }.getOrDefault(State())
    }

    /** Marks the state dirty; it is written within [FLUSH_DELAY_MS]. */
    private fun save() {
        if (file == null) return
        dirty = true
        if (flushScheduled) return
        flushScheduled = true
        runCatching { flusher.schedule({ flush() }, FLUSH_DELAY_MS, java.util.concurrent.TimeUnit.MILLISECONDS) }
    }

    /** Writes pending changes now (also on [close]). */
    fun flush() {
        val f = file ?: return
        val json = synchronized(lock) {
            flushScheduled = false
            if (!dirty) return
            dirty = false
            BridgeJson.encodeToString(State.serializer(), State(outbox = state.outbox.toMutableList())) // completed/batches live in their journals
        }
        synchronized(f) { // one writer at a time; the state lock isn't held during disk I/O
            runCatching {
                f.parentFile?.mkdirs()
                val tmp = File(f.path + ".tmp")
                tmp.writeText(json)
                if (!tmp.renameTo(f)) { f.delete(); tmp.renameTo(f) }
            } // best effort: resume after an app restart is lost, resume after a reconnect still works
        }
    }

    override fun close() {
        flusher.shutdown()
        flush()
    }
}

/**
 * Maps a peer-supplied folder path (file.start relativePath, '/'-separated) to
 * a directory inside the download folder, mirroring Bridge.Network.SafeRelativePath.
 */
object SafeRelativePath {
    const val MAX_DEPTH = 16

    /** Sanitised segments, or null if the path is too deep. */
    fun segments(relativePath: String?): List<String>? {
        val parts = (relativePath ?: "").replace('\\', '/').split('/')
            .map { it.trim() }.filter { it.isNotEmpty() && it != "." && it != ".." }
            .map(SafeFileName::sanitize)
        return if (parts.size > MAX_DEPTH) null else parts
    }

    /** Directory for a file of a folder transfer whose top folder was named [rootName]. */
    fun resolve(downloadDir: File, rootName: String, relativePath: String?): File? {
        val segs = segments(relativePath) ?: return null
        var dir = File(downloadDir, rootName)
        for (s in segs.drop(1)) dir = File(dir, s)
        val root = downloadDir.canonicalPath + File.separator
        return if (dir.canonicalPath.startsWith(root)) dir else null // defence in depth
    }

    fun uniqueRootName(downloadDir: File, name: String): String {
        if (!File(downloadDir, name).exists()) return name
        var i = 1
        while (File(downloadDir, "$name ($i)").exists()) i++
        return "$name ($i)"
    }
}
