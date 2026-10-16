package com.bridge.app.network

import com.bridge.app.protocol.*
import com.bridge.app.security.FileHasher
import java.io.File
import java.io.IOException
import java.io.RandomAccessFile
import java.security.MessageDigest
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.selects.select
import kotlinx.coroutines.withContext
import kotlinx.coroutines.withTimeoutOrNull

enum class TransferDirection { SENDING, RECEIVING }

/** [INTERRUPTED]: the connection dropped; the transfer continues from where it stopped on reconnect. */
enum class TransferStatus { IN_PROGRESS, COMPLETED, FAILED, CANCELLED, INTERRUPTED }

data class TransferProgress(
    val transferId: String,
    val fileName: String,
    val direction: TransferDirection,
    val status: TransferStatus = TransferStatus.IN_PROGRESS,
    val chunksDone: Int = 0,
    val totalChunks: Int = 0,
    /** For completed incoming transfers, where the file was saved. */
    val savedFile: File? = null,
    /** Folder transfers: the file's folder inside the batch, and its place in the batch. */
    val relativePath: String? = null,
    val batchId: String? = null,
    val batchIndex: Int? = null,
    val batchCount: Int? = null,
    /** Chunk the transfer (re)started from: > 0 when it was resumed. */
    val resumedFrom: Int = 0,
    /** Sending: the local file being sent (for "Retry"). */
    val sourceFile: File? = null,
    /** Sending: a failed send's private copy was kept so it can be retried (see [FileTransferClient.retainFailedCopies]). */
    val retainedCopy: Boolean = false,
) {
    val fraction: Double
        get() = if (totalChunks == 0) (if (status == TransferStatus.COMPLETED) 1.0 else 0.0) else chunksDone.toDouble() / totalChunks
}

/** What the PC supports, for outgoing transfers. */
data class SendOptions(val peerId: String, val peerSupportsResume: Boolean, val peerSupportsFolders: Boolean)

/**
 * One file of a folder transfer: [relativePath] is its folder ('/'-separated,
 * starting with the top folder's name); [name] is the name the PC sees (the
 * file on disk may be a private copy with another name).
 */
data class FolderItem(val file: File, val relativePath: String, val deleteWhenDone: Boolean = false, val name: String = file.name)

/**
 * Mirrors Bridge.Network.FileTransferService on Windows (protocol/protocol.md).
 * Receiving: names and folder paths are sanitised, never overwrite, chunks in
 * order and within the announced size; resumable partials are kept in a
 * hidden ".bridge-partial" folder across disconnects for 24 h.
 * Sending: resumable sends stay in the [state] outbox until the PC acknowledges
 * them and are offered again on the next connection ([resumePending]).
 */
class FileTransferClient(
    val state: TransferStateStore = TransferStateStore(),
    private val onProgress: (TransferProgress) -> Unit,
) {
    /** Kept for existing callers: in-memory resume state. */
    constructor(onProgress: (TransferProgress) -> Unit) : this(TransferStateStore(), onProgress)

    companion object {
        const val CHUNK_SIZE = 64 * 1024
        const val MAX_CONCURRENT_INCOMING = 8
        const val MAX_FOLDER_FILES = 10_000
        const val PARTIAL_DIR_NAME = ".bridge-partial"
        /** Abandoned resumable partials kept per peer; older ones are evicted. */
        const val MAX_PARTIALS_PER_PEER = 32
        const val RESUME_REPLY_TIMEOUT_MS = 30_000L

        /** How long a failed send's private copy is kept for "Retry". */
        const val RETAINED_COPY_MS = 24L * 60 * 60 * 1000

        /**
         * Deletes private copies in [dir] older than [maxAgeMs] that no queued send needs
         * ([inUse] = [TransferStateStore.pendingPaths]). Call at startup.
         */
        fun sweepStaleCopies(dir: File, inUse: Set<String>, maxAgeMs: Long = RETAINED_COPY_MS, now: Long = System.currentTimeMillis()): Int {
            var n = 0
            dir.listFiles()?.forEach { f ->
                if (f.isFile && f.path !in inUse && now - f.lastModified() > maxAgeMs && f.delete()) n++
            }
            return n
        }

        /** 60 s plus the time to hash the file at a slow 50 MB/s. */
        fun finalAckTimeoutMs(size: Long): Long = 60_000L + size * 1000 / (50L * 1024 * 1024)

        /** Every file under [root] for [sendFolder] (no symlinks, depth-capped, sorted). */
        fun folderItems(root: File): List<FolderItem> = root.walkTopDown()
            .onEnter { dir -> dir == root || !java.nio.file.Files.isSymbolicLink(dir.toPath()) }
            .maxDepth(SafeRelativePath.MAX_DEPTH)
            .filter { it.isFile && !java.nio.file.Files.isSymbolicLink(it.toPath()) }
            .sortedBy { it.path }
            .map { f -> FolderItem(f, (listOf(root.name) + f.parentFile!!.relativeTo(root).invariantSeparatorsPath.split('/').filter { it.isNotEmpty() }).joinToString("/")) }
            .toList()

        /** Deletes resumable partial files nobody came back for within 24 h. */
        fun cleanupExpiredPartials(downloadDir: File, now: Long = System.currentTimeMillis()) {
            // Non-resumable temp files left behind if the app was killed mid-transfer (only ours: ".bridge-*.part").
            downloadDir.listFiles { f -> f.isFile && f.name.startsWith(".bridge-") && f.name.endsWith(".part") }.orEmpty()
                .filter { it.lastModified() < now - TransferStateStore.LIFETIME_MS }.forEach { it.delete() }
            val dir = File(downloadDir, PARTIAL_DIR_NAME)
            val files = dir.listFiles() ?: return
            for ((stem, group) in files.groupBy { it.nameWithoutExtension }) {
                val meta = File(dir, "$stem.json")
                val stamp = if (meta.exists()) meta.lastModified() else group.maxOf { it.lastModified() }
                if (stamp < now - TransferStateStore.LIFETIME_MS) group.forEach { it.delete() }
            }
        }
    }

    private class Incoming(
        val channel: EncryptedChannel,
        val peerId: String,
        val meta: FileStartPayload,
        val safeName: String,
        val temp: File,
        val metaFile: File?, // resumable only
        val out: RandomAccessFile,
        var nextIndex: Int = 0,
        var bytesWritten: Long = 0,
        val resumedFrom: Int = 0,
    ) {
        val resumable get() = metaFile != null
    }

    /** An outgoing transfer, kept from file.start until the receiver's file.ack. */
    /** Keep a failed send's private copy (deleteWhenDone) for "Retry" instead of deleting it. */
    @Volatile var retainFailedCopies: Boolean = false

    private class Outgoing(val channel: EncryptedChannel, @Volatile var progress: TransferProgress) {
        val resumeReply = CompletableDeferred<Int>()    // file.resume nextIndex; −1 = receiver already has it
        val finalAck = CompletableDeferred<String>()    // file.ack after file.end (resumable)
        val abort = CompletableDeferred<Unit>()         // user cancel, peer verdict or disconnect
        @Volatile var userCancelled = false
        @Volatile var disconnected = false
        var peerVerdict: String? = null                 // non-ok file.ack status (or peer file.cancel)
        var awaitingFinalAck = false
        var finished = false                            // sender loop has returned
    }

    private val outgoing = ConcurrentHashMap<String, Outgoing>()
    private val incoming = mutableMapOf<String, Incoming>() // only touched by a session's receive loop
    private val incomingLock = Any()

    // ------------------------------------------------------------------ sending

    /** Sends a file; returns the final status instead of throwing for I/O problems, rejection or user cancellation. */
    suspend fun sendFile(
        channel: EncryptedChannel,
        file: File,
        displayName: String = file.name,
        options: SendOptions = SendOptions(channel.peerDeviceId, false, false),
        deleteWhenDone: Boolean = false,
        /** Lets a caller announce the transfer before it starts (photos.fetched). */
        transferId: String = UUID.randomUUID().toString(),
    ): TransferProgress = sendEntry(channel, TransferStateStore.OutboxEntry(transferId, options.peerId,
        file.path, displayName, created = System.currentTimeMillis(), deleteWhenDone = deleteWhenDone), options)

    /**
     * Sends [items] as one folder batch (structure kept when the PC supports
     * folders; otherwise as loose files). If the connection drops, the rest of
     * the batch is queued so the whole folder resumes later.
     */
    suspend fun sendFolder(channel: EncryptedChannel, items: List<FolderItem>, options: SendOptions): List<TransferProgress> {
        val batchId = UUID.randomUUID().toString()
        fun entry(i: Int) = TransferStateStore.OutboxEntry(
            UUID.randomUUID().toString(), options.peerId, items[i].file.path, items[i].name,
            relativePath = if (options.peerSupportsFolders) items[i].relativePath else null,
            batchId = if (options.peerSupportsFolders) batchId else null,
            batchIndex = if (options.peerSupportsFolders) i else null,
            batchCount = if (options.peerSupportsFolders) items.size else null,
            created = System.currentTimeMillis(), deleteWhenDone = items[i].deleteWhenDone)

        val results = mutableListOf<TransferProgress>()
        var attempted = 0   // items [0, attempted) belong to sendEntry, which tidies their copies
        var queued = false  // items [attempted, n) were queued for the next connection
        try {
            for (i in items.indices) {
                attempted = i + 1
                val result = sendEntry(channel, entry(i), options)
                results += result
                if (result.status == TransferStatus.INTERRUPTED || !channel.isConnected) {
                    // Connection gone: queue the rest so the whole folder resumes later.
                    if (options.peerSupportsResume) { state.addOutboxAll((i + 1 until items.size).map(::entry)); queued = true }
                    break
                }
            }
        } finally {
            // Private copies of files neither sent nor queued would otherwise stay in cache forever.
            if (!queued) for (j in attempted until items.size) if (items[j].deleteWhenDone) items[j].file.delete()
        }
        return results
    }

    /** Offers again every unacknowledged send to [options].peerId (call when a session starts). */
    suspend fun resumePending(channel: EncryptedChannel, options: SendOptions) {
        if (!options.peerSupportsResume) return
        for (entry in state.pendingFor(options.peerId).sortedBy { it.created }) {
            if (!channel.isConnected) return
            if (outgoing.containsKey(entry.transferId)) continue
            if (sendEntry(channel, entry, options).status == TransferStatus.INTERRUPTED) return
        }
    }

    /** User cancel: stops an active send, or drops a queued one. */
    fun cancelTransfer(transferId: String) {
        state.removeOutbox(transferId)
        outgoing[transferId]?.let { o -> o.userCancelled = true; o.abort.complete(Unit) }
    }

    /** Waits for [d], giving up on abort or timeout (null). */
    private suspend fun <T> awaitOrAbort(d: CompletableDeferred<T>, o: Outgoing, timeoutMs: Long): T? =
        withTimeoutOrNull(timeoutMs) { select { d.onAwait { it }; o.abort.onAwait { null } } }

    private suspend fun sendEntry(channel: EncryptedChannel, entry: TransferStateStore.OutboxEntry, options: SendOptions): TransferProgress {
        val resumable = options.peerSupportsResume
        val id = entry.transferId
        val file = File(entry.filePath)
        val o = Outgoing(channel, TransferProgress(id, entry.fileName, TransferDirection.SENDING, sourceFile = file,
            relativePath = entry.relativePath, batchId = entry.batchId, batchIndex = entry.batchIndex, batchCount = entry.batchCount))
        outgoing[id] = o
        if (resumable) state.addOutbox(entry)

        var started = false
        var localError = false
        var status: TransferStatus
        try {
            val (hash, size) = try {
                withContext(Dispatchers.IO) {
                    if (!file.exists()) throw java.io.FileNotFoundException(file.path)
                    val size = file.length()
                    val stamp = file.lastModified()
                    val cached = entry.sha256
                    if (cached != null && entry.size == size && entry.lastModified == stamp) cached to size // unchanged: don't re-read a big file on every reconnect
                    else FileHasher.sha256HexOfFile(file.path).also { h ->
                        if (resumable) state.addOutbox(entry.copy(sha256 = h, size = size, lastModified = stamp))
                    } to size
                }
            } catch (e: IOException) { localError = true; throw e }
            val totalChunks = ((size + CHUNK_SIZE - 1) / CHUNK_SIZE).toInt()
            o.progress = o.progress.copy(totalChunks = totalChunks)
            onProgress(o.progress)

            channel.send(BridgeMessage.create(MessageType.FILE_START, FileStartPayload(id, entry.fileName, size, hash, totalChunks,
                resumable = if (resumable) true else null, relativePath = entry.relativePath,
                batchId = entry.batchId, batchIndex = entry.batchIndex, batchCount = entry.batchCount)))
            started = true

            var startIndex = 0
            status = TransferStatus.IN_PROGRESS
            if (resumable) {
                val reply = awaitOrAbort(o.resumeReply, o, RESUME_REPLY_TIMEOUT_MS)
                when {
                    reply == null -> status = if (o.disconnected) TransferStatus.INTERRUPTED else if (o.abort.isCompleted) TransferStatus.CANCELLED else TransferStatus.FAILED
                    reply == -1 -> { // the PC already stored this file (our earlier ack was lost)
                        o.progress = o.progress.copy(chunksDone = totalChunks)
                        status = TransferStatus.COMPLETED
                    }
                    reply < 0 || reply > totalChunks -> status = TransferStatus.FAILED
                    else -> { startIndex = reply; o.progress = o.progress.copy(resumedFrom = reply, chunksDone = reply) }
                }
            }

            if (status == TransferStatus.IN_PROGRESS) {
                val stopped = withContext(Dispatchers.IO) { RandomAccessFile(file, "r") }.use { input ->
                    input.seek(startIndex.toLong() * CHUNK_SIZE)
                    val buffer = ByteArray(CHUNK_SIZE)
                    for (index in startIndex until totalChunks) {
                        if (o.abort.isCompleted) return@use true
                        val read = try { withContext(Dispatchers.IO) { readFully(input, buffer) } } catch (e: IOException) { localError = true; throw e }
                        if (read == 0) { localError = true; throw IOException("File shrank while it was being sent") }
                        channel.send(BridgeMessage.create(MessageType.FILE_CHUNK,
                            FileChunkPayload(id, index, Base64.getEncoder().encodeToString(buffer.copyOf(read)))))
                        o.progress = o.progress.copy(chunksDone = index + 1)
                        onProgress(o.progress)
                    }
                    false
                }
                status = if (stopped) aborted(o) else {
                    synchronized(o) { o.awaitingFinalAck = true } // the ack can arrive before send() returns
                    channel.send(BridgeMessage.create(MessageType.FILE_END, FileEndPayload(id)))
                    if (resumable) {
                        // Only "sent" once the PC confirms it stored the file intact.
                        when (val ack = awaitOrAbort(o.finalAck, o, finalAckTimeoutMs(size))) {
                            null -> if (o.abort.isCompleted) aborted(o) else TransferStatus.INTERRUPTED // no answer: stays queued
                            FileAckStatus.OK -> TransferStatus.COMPLETED
                            else -> { synchronized(o) { o.peerVerdict = ack }; statusFor(ack) }
                        }
                    } else TransferStatus.COMPLETED
                }
            }
        } catch (e: CancellationException) {
            // Our coroutine is being cancelled (service stopping): keep resumable sends queued.
            synchronized(o) { o.finished = true }
            outgoing.remove(id)
            if (!resumable) { if (started) trySendCancel(channel, id); state.removeOutbox(id); if (entry.deleteWhenDone) file.delete() }
            throw e
        } catch (e: IOException) {
            // A lost connection is resumable; a local file problem is not.
            status = if (resumable && !localError && !channel.isConnected) TransferStatus.INTERRUPTED else TransferStatus.FAILED
        }

        val peerAborted = synchronized(o) {
            o.finished = true
            val v = o.peerVerdict
            o.progress = o.progress.copy(status = if (v != null) statusFor(v) else status)
            v != null
        }
        when {
            o.progress.status == TransferStatus.COMPLETED && resumable -> { outgoing.remove(id); state.removeOutbox(id) }
            o.progress.status == TransferStatus.COMPLETED -> Unit // legacy peer: wait for file.ack
            o.progress.status == TransferStatus.INTERRUPTED -> outgoing.remove(id) // outbox entry kept
            else -> {
                if (started && !peerAborted) trySendCancel(channel, id)
                outgoing.remove(id)
                state.removeOutbox(id)
            }
        }
        // A private copy is kept while an interrupted send waits in the outbox, and (with
        // retainFailedCopies) after a failure, so "Retry" can send it again; sweepStaleCopies
        // removes forgotten ones. Folder files aren't retried one by one.
        val keepForRetry = retainFailedCopies && entry.deleteWhenDone && entry.batchId == null &&
            (o.progress.status == TransferStatus.FAILED || (o.progress.status == TransferStatus.CANCELLED && !o.userCancelled))
        if (keepForRetry) o.progress = o.progress.copy(retainedCopy = true)
        else if (entry.deleteWhenDone && !(resumable && o.progress.status == TransferStatus.INTERRUPTED)) file.delete()
        onProgress(o.progress)
        return o.progress
    }

    private fun aborted(o: Outgoing) = when {
        o.disconnected -> TransferStatus.INTERRUPTED
        o.userCancelled -> TransferStatus.CANCELLED
        else -> TransferStatus.CANCELLED // peer verdict, applied by the caller
    }

    private fun statusFor(ackStatus: String) =
        if (ackStatus == FileAckStatus.CANCELLED) TransferStatus.CANCELLED else TransferStatus.FAILED

    // ---------------------------------------------------------------- receiving

    /** Feed every incoming file.* message here; completed files land in [downloadDir]. */
    suspend fun handleIncoming(channel: EncryptedChannel, message: BridgeMessage, downloadDir: File): TransferProgress? {
        return when (message.type) {
            MessageType.FILE_START -> handleStart(channel, message.payloadAs(), downloadDir)
            MessageType.FILE_CHUNK -> {
                val chunk = message.payloadAs<FileChunkPayload>()
                val s = synchronized(incomingLock) { incoming[chunk.transferId] } ?: return null
                val bytes = try { Base64.getDecoder().decode(chunk.data) } catch (e: IllegalArgumentException) {
                    return failIncoming(channel, s)
                }
                if (chunk.index != s.nextIndex || s.bytesWritten + bytes.size > s.meta.size) return failIncoming(channel, s)
                try {
                    withContext(Dispatchers.IO) { s.out.write(bytes) }
                } catch (e: IOException) {
                    return failIncoming(channel, s, FileAckStatus.CANCELLED) // storage full / removed
                }
                s.nextIndex++
                s.bytesWritten += bytes.size
                report(progress(s, TransferStatus.IN_PROGRESS))
            }
            MessageType.FILE_END -> handleEnd(channel, message.payloadAs(), downloadDir)
            MessageType.FILE_CANCEL -> {
                val cancel = message.payloadAs<FileCancelPayload>()
                outgoing[cancel.transferId]?.let { o -> // the PC may be cancelling something we're sending…
                    synchronized(o) { if (!o.finished) o.peerVerdict = FileAckStatus.CANCELLED }
                    o.abort.complete(Unit)
                }
                state.removeOutbox(cancel.transferId)
                val s = synchronized(incomingLock) { incoming.remove(cancel.transferId) } ?: return null // …or something it was sending us
                withContext(Dispatchers.IO) { runCatching { s.out.close() }; deletePartial(s) }
                report(progress(s, TransferStatus.CANCELLED))
            }
            MessageType.FILE_RESUME -> {
                val r = message.payloadAs<FileResumePayload>()
                outgoing[r.transferId]?.resumeReply?.complete(r.nextIndex)
                null
            }
            MessageType.FILE_ACK -> {
                val ack = message.payloadAs<FileAckPayload>()
                val o = outgoing[ack.transferId] ?: run { if (ack.status == FileAckStatus.OK) state.removeOutbox(ack.transferId); return null }
                synchronized(o) {
                    if (o.awaitingFinalAck && !o.finished && o.resumeReply.isCompleted) { o.finalAck.complete(ack.status); return null }
                }
                if (ack.status == FileAckStatus.OK) {
                    synchronized(o) { if (!o.finished) { o.resumeReply.complete(-1); return null } } // already stored
                    outgoing.remove(ack.transferId)
                    state.removeOutbox(ack.transferId)
                    return o.progress
                }
                synchronized(o) {
                    o.peerVerdict = ack.status
                    if (!o.finished) { o.abort.complete(Unit); return null } // still sending: stop; it reports the verdict
                    o.progress = o.progress.copy(status = statusFor(ack.status))
                }
                outgoing.remove(ack.transferId)
                state.removeOutbox(ack.transferId)
                report(o.progress)
            }
            else -> null
        }
    }

    private suspend fun handleStart(channel: EncryptedChannel, meta: FileStartPayload, downloadDir: File): TransferProgress? {
        val peerId = channel.peerDeviceId
        val busy = synchronized(incomingLock) { meta.transferId in incoming || incoming.size >= MAX_CONCURRENT_INCOMING }
        if (meta.transferId.isEmpty() || meta.size < 0 || meta.totalChunks < 0 ||
            meta.totalChunks.toLong() != (meta.size + CHUNK_SIZE - 1) / CHUNK_SIZE || busy ||
            (meta.relativePath != null && SafeRelativePath.segments(meta.relativePath).isNullOrEmpty())
        ) {
            sendAck(channel, meta.transferId, FileAckStatus.CANCELLED)
            return null
        }
        val safeName = SafeFileName.sanitize(meta.fileName)
        val resumable = meta.resumable == true

        if (resumable) {
            val saved = state.completedPath(peerId, meta.transferId, meta.sha256)
            if (saved != null && File(saved).exists()) {
                // Re-offer of a file we already stored (the PC missed our ack): don't store it twice.
                sendAck(channel, meta.transferId, FileAckStatus.OK)
                return report(TransferProgress(meta.transferId, safeName, TransferDirection.RECEIVING, TransferStatus.COMPLETED,
                    meta.totalChunks, meta.totalChunks, File(saved), meta.relativePath, meta.batchId, meta.batchIndex, meta.batchCount))
            }
        }

        val s = try {
            withContext(Dispatchers.IO) {
                downloadDir.mkdirs()
                if (resumable) openResumable(channel, peerId, meta, safeName, downloadDir) else openFresh(channel, peerId, meta, safeName, downloadDir)
            }
        } catch (e: IOException) {
            sendAck(channel, meta.transferId, FileAckStatus.CANCELLED) // can't store it: tell the sender now
            return report(TransferProgress(meta.transferId, safeName, TransferDirection.RECEIVING, TransferStatus.FAILED))
        }
        synchronized(incomingLock) { incoming[meta.transferId] = s }
        if (resumable) channel.send(BridgeMessage.create(MessageType.FILE_RESUME, FileResumePayload(meta.transferId, s.nextIndex)))
        return report(progress(s, TransferStatus.IN_PROGRESS))
    }

    private fun openFresh(channel: EncryptedChannel, peerId: String, meta: FileStartPayload, safeName: String, downloadDir: File): Incoming {
        val temp = File(downloadDir, ".bridge-${UUID.randomUUID()}.part")
        return Incoming(channel, peerId, meta, safeName, temp, null, RandomAccessFile(temp, "rw"))
    }

    /** Reopens a matching partial from an earlier connection, or starts a new resumable one. */
    private fun openResumable(channel: EncryptedChannel, peerId: String, meta: FileStartPayload, safeName: String, downloadDir: File): Incoming {
        val dir = File(downloadDir, PARTIAL_DIR_NAME).apply { mkdirs() }
        // "<peer prefix>-<peer+transfer hash>": the prefix lets us cap partials per peer.
        val peerPrefix = hex(peerId).take(8)
        val key = peerPrefix + "-" + hex(TransferStateStore.key(peerId, meta.transferId)).take(24)
        val part = File(dir, "$key.part")
        val metaFile = File(dir, "$key.json")
        val metaJson = BridgeJson.encodeToString(FileStartPayload.serializer(), meta)
        val reuse = part.exists() && metaFile.exists() && runCatching { metaFile.readText() }.getOrNull() == metaJson
        if (!reuse) {
            part.delete()
            // Keep at most MAX_PARTIALS_PER_PEER abandoned partials from one peer (oldest go first).
            dir.listFiles { f -> f.name.startsWith("$peerPrefix-") && f.name.endsWith(".json") }.orEmpty()
                .sortedByDescending { it.lastModified() }.drop(MAX_PARTIALS_PER_PEER - 1)
                .forEach { old -> old.delete(); File(dir, old.nameWithoutExtension + ".part").delete() }
            metaFile.writeText(metaJson)
        }

        val out = RandomAccessFile(part, "rw")
        val length = out.length()
        val (nextIndex, kept) = when {
            length > meta.size -> 0 to 0L                                   // corrupt: start over
            length == meta.size -> meta.totalChunks to length
            else -> (length / CHUNK_SIZE).toInt().let { it to it.toLong() * CHUNK_SIZE } // drop a half-written chunk
        }
        out.setLength(kept)
        out.seek(kept)
        metaFile.setLastModified(System.currentTimeMillis()) // keeps the partial alive another 24 h
        return Incoming(channel, peerId, meta, safeName, part, metaFile, out, nextIndex, kept, nextIndex)
    }

    private suspend fun handleEnd(channel: EncryptedChannel, end: FileEndPayload, downloadDir: File): TransferProgress? {
        val s = synchronized(incomingLock) { incoming.remove(end.transferId) } ?: return null
        val finalFile = try {
            val actualHash = withContext(Dispatchers.IO) { s.out.close(); FileHasher.sha256HexOfFile(s.temp.path) }
            if (s.bytesWritten != s.meta.size || !actualHash.equals(s.meta.sha256, ignoreCase = true)) {
                deletePartial(s)
                sendAck(channel, end.transferId, FileAckStatus.HASH_MISMATCH)
                return report(progress(s, TransferStatus.FAILED))
            }
            withContext(Dispatchers.IO) {
                var targetDir = downloadDir
                s.meta.relativePath?.let { rel ->
                    val top = SafeRelativePath.segments(rel)!![0]
                    val root = state.batchRootName(s.peerId, s.meta.batchId ?: s.meta.transferId) { SafeRelativePath.uniqueRootName(downloadDir, top) }
                    targetDir = SafeRelativePath.resolve(downloadDir, root, rel) ?: throw IOException("Unsafe folder path")
                    if (!targetDir.isDirectory && !targetDir.mkdirs()) throw IOException("Couldn't create folder")
                }
                SafeFileName.uniqueFile(targetDir, s.safeName).also { target ->
                    if (!s.temp.renameTo(target)) throw IOException("Couldn't save ${s.safeName}")
                    s.metaFile?.delete()
                    if (s.resumable) state.markCompleted(s.peerId, s.meta.transferId, target.path, s.meta.sha256)
                }
            }
        } catch (e: IOException) {
            deletePartial(s) // couldn't finish storing it: the sender must not believe it arrived
            sendAck(channel, end.transferId, FileAckStatus.CANCELLED)
            return report(progress(s, TransferStatus.FAILED))
        }
        sendAck(channel, end.transferId, FileAckStatus.OK)
        return report(progress(s, TransferStatus.COMPLETED).copy(savedFile = finalFile))
    }

    /**
     * Call when a session ends. Resumable partials are kept (reported
     * INTERRUPTED); others are deleted. Sends on this channel stop: resumable
     * ones become INTERRUPTED and stay queued.
     */
    fun abortAll(channel: EncryptedChannel) {
        val mine = synchronized(incomingLock) {
            incoming.values.filter { it.channel === channel }.also { list -> list.forEach { incoming.remove(it.meta.transferId) } }
        }
        for (s in mine) {
            runCatching { s.out.close() }
            if (s.resumable) report(progress(s, TransferStatus.INTERRUPTED)) else { s.temp.delete(); report(progress(s, TransferStatus.FAILED)) }
        }
        for ((id, o) in outgoing) {
            if (o.channel !== channel) continue
            val wasFinished = synchronized(o) { if (!o.finished) { o.disconnected = true; o.abort.complete(Unit) }; o.finished }
            if (wasFinished) outgoing.remove(id) // sent, ack lost: the outbox (if resumable) re-offers it
        }
    }

    // ------------------------------------------------------------------ helpers

    private suspend fun failIncoming(channel: EncryptedChannel, s: Incoming, status: String = FileAckStatus.HASH_MISMATCH): TransferProgress {
        synchronized(incomingLock) { incoming.remove(s.meta.transferId) }
        withContext(Dispatchers.IO) { runCatching { s.out.close() }; deletePartial(s) }
        sendAck(channel, s.meta.transferId, status)
        return report(progress(s, TransferStatus.FAILED))
    }

    private fun hex(value: String) = MessageDigest.getInstance("SHA-256").digest(value.toByteArray()).joinToString("") { "%02x".format(it) }

    private fun deletePartial(s: Incoming) { s.temp.delete(); s.metaFile?.delete() }

    private suspend fun sendAck(channel: EncryptedChannel, id: String, status: String) =
        channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(id, status)))

    private fun progress(s: Incoming, status: TransferStatus) = TransferProgress(
        s.meta.transferId, s.safeName, TransferDirection.RECEIVING, status, s.nextIndex, s.meta.totalChunks, null,
        s.meta.relativePath, s.meta.batchId, s.meta.batchIndex, s.meta.batchCount, s.resumedFrom)

    private fun report(progress: TransferProgress): TransferProgress = progress.also(onProgress)

    private fun readFully(input: RandomAccessFile, buffer: ByteArray): Int {
        var total = 0
        while (total < buffer.size) {
            val read = input.read(buffer, total, buffer.size - total)
            if (read <= 0) break
            total += read
        }
        return total
    }

    private suspend fun trySendCancel(channel: EncryptedChannel, transferId: String) {
        withContext(NonCancellable) {
            runCatching { channel.send(BridgeMessage.create(MessageType.FILE_CANCEL, FileCancelPayload(transferId))) }
        }
    }
}
