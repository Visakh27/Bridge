package com.bridge.app.network

import com.bridge.app.protocol.*
import com.bridge.app.security.FileHasher
import java.io.File
import java.io.FileOutputStream
import java.io.IOException
import java.util.Base64
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.NonCancellable
import kotlinx.coroutines.withContext

enum class TransferDirection { SENDING, RECEIVING }
enum class TransferStatus { IN_PROGRESS, COMPLETED, FAILED, CANCELLED }

data class TransferProgress(
    val transferId: String,
    val fileName: String,
    val direction: TransferDirection,
    val status: TransferStatus = TransferStatus.IN_PROGRESS,
    val chunksDone: Int = 0,
    val totalChunks: Int = 0,
    /** For completed incoming transfers, where the file was saved. */
    val savedFile: File? = null,
) {
    val fraction: Double
        get() = if (totalChunks == 0) (if (status == TransferStatus.COMPLETED) 1.0 else 0.0) else chunksDone.toDouble() / totalChunks
}

/**
 * Mirrors Bridge.Network.FileTransferService on Windows. Chunk size must match
 * (see protocol/protocol.md). Incoming names are sanitised and never
 * overwrite existing files; chunks must arrive in order and never exceed the
 * announced size.
 */
class FileTransferClient(private val onProgress: (TransferProgress) -> Unit) {
    companion object {
        const val CHUNK_SIZE = 64 * 1024
        const val MAX_CONCURRENT_INCOMING = 8
    }

    private class Incoming(
        val meta: FileStartPayload,
        val safeName: String,
        val temp: File,
        val stream: FileOutputStream,
        var nextIndex: Int = 0,
        var bytesWritten: Long = 0,
    )

    /**
     * An outgoing transfer, kept from file.start until the receiver's file.ack.
     * The receiver may ack early (rejecting file.start, or failing mid-transfer);
     * that verdict stops the sender instead of being ignored.
     */
    private class Outgoing(@Volatile var progress: TransferProgress) {
        @Volatile var userCancelled = false
        var peerVerdict: String? = null // non-ok file.ack status (or peer file.cancel)
        var finished = false            // sender loop has returned
    }

    private val outgoing = ConcurrentHashMap<String, Outgoing>()
    private val incoming = mutableMapOf<String, Incoming>() // only touched by the session's receive loop

    /** Sends a file; returns the final status instead of throwing for I/O problems, rejection or user cancellation. */
    suspend fun sendFile(channel: EncryptedChannel, file: File, displayName: String = file.name): TransferProgress {
        val transferId = UUID.randomUUID().toString()
        val state = Outgoing(TransferProgress(transferId, displayName, TransferDirection.SENDING))
        outgoing[transferId] = state
        var started = false
        var status: TransferStatus
        try {
            val size = file.length()
            val hash = withContext(Dispatchers.IO) { FileHasher.sha256HexOfFile(file.path) }
            val totalChunks = ((size + CHUNK_SIZE - 1) / CHUNK_SIZE).toInt()
            state.progress = state.progress.copy(totalChunks = totalChunks)
            onProgress(state.progress)

            channel.send(BridgeMessage.create(MessageType.FILE_START,
                FileStartPayload(transferId, displayName, size, hash, totalChunks)))
            started = true

            withContext(Dispatchers.IO) { file.inputStream().buffered(CHUNK_SIZE) }.use { input ->
                val buffer = ByteArray(CHUNK_SIZE)
                for (index in 0 until totalChunks) {
                    if (state.userCancelled || synchronized(state) { state.peerVerdict != null }) break
                    val read = withContext(Dispatchers.IO) { readFully(input, buffer) }
                    if (read == 0) throw IOException("File shrank while it was being sent")
                    val chunkB64 = Base64.getEncoder().encodeToString(buffer.copyOf(read))
                    channel.send(BridgeMessage.create(MessageType.FILE_CHUNK, FileChunkPayload(transferId, index, chunkB64)))
                    state.progress = state.progress.copy(chunksDone = index + 1)
                    onProgress(state.progress)
                }
            }

            status = if (state.userCancelled) TransferStatus.CANCELLED else {
                if (synchronized(state) { state.peerVerdict == null }) {
                    channel.send(BridgeMessage.create(MessageType.FILE_END, FileEndPayload(transferId)))
                }
                TransferStatus.COMPLETED
            }
        } catch (e: IOException) {
            status = TransferStatus.FAILED
        } catch (e: CancellationException) {
            // Our coroutine is being cancelled (service stopping): clean up, then propagate.
            finish(state, TransferStatus.CANCELLED)
            if (started) trySendCancel(channel, transferId)
            outgoing.remove(transferId)
            throw e
        }

        val peerAborted = finish(state, status)
        // Tell the receiver to drop its partial file, unless it already gave up on its own.
        if (started && !peerAborted && state.progress.status != TransferStatus.COMPLETED) trySendCancel(channel, transferId)
        if (state.progress.status != TransferStatus.COMPLETED) outgoing.remove(transferId) // else: await file.ack
        onProgress(state.progress)
        return state.progress
    }

    /** Marks the sender finished and settles its status; returns true if the peer had already aborted. */
    private fun finish(state: Outgoing, status: TransferStatus): Boolean = synchronized(state) {
        state.finished = true
        val verdict = state.peerVerdict
        state.progress = state.progress.copy(status = if (verdict != null) statusFor(verdict) else status)
        verdict != null
    }

    fun cancelTransfer(transferId: String) {
        outgoing[transferId]?.userCancelled = true
    }

    private fun statusFor(ackStatus: String) =
        if (ackStatus == FileAckStatus.CANCELLED) TransferStatus.CANCELLED else TransferStatus.FAILED

    /** Feed every incoming file.* message here; completed files land in [downloadDir]. */
    suspend fun handleIncoming(channel: EncryptedChannel, message: BridgeMessage, downloadDir: File): TransferProgress? {
        return when (message.type) {
            MessageType.FILE_START -> {
                val meta = message.payloadAs<FileStartPayload>()
                if (meta.transferId.isEmpty() || meta.size < 0 || meta.totalChunks < 0 ||
                    meta.transferId in incoming || incoming.size >= MAX_CONCURRENT_INCOMING
                ) {
                    channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(meta.transferId, FileAckStatus.CANCELLED)))
                    return null
                }
                val safeName = SafeFileName.sanitize(meta.fileName)
                val state = try {
                    withContext(Dispatchers.IO) {
                        downloadDir.mkdirs()
                        val temp = File(downloadDir, ".bridge-${UUID.randomUUID()}.part")
                        Incoming(meta, safeName, temp, FileOutputStream(temp))
                    }
                } catch (e: IOException) {
                    // Can't store it (storage full, unmounted): tell the sender now.
                    channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(meta.transferId, FileAckStatus.CANCELLED)))
                    return report(TransferProgress(meta.transferId, safeName, TransferDirection.RECEIVING, TransferStatus.FAILED))
                }
                incoming[meta.transferId] = state
                report(TransferProgress(meta.transferId, safeName, TransferDirection.RECEIVING, totalChunks = meta.totalChunks))
            }
            MessageType.FILE_CHUNK -> {
                val chunk = message.payloadAs<FileChunkPayload>()
                val state = incoming[chunk.transferId] ?: return null
                val bytes = try { Base64.getDecoder().decode(chunk.data) } catch (e: IllegalArgumentException) {
                    return failIncoming(channel, state)
                }
                if (chunk.index != state.nextIndex || state.bytesWritten + bytes.size > state.meta.size) {
                    return failIncoming(channel, state)
                }
                try {
                    withContext(Dispatchers.IO) { state.stream.write(bytes) }
                } catch (e: IOException) {
                    // Storage full / removed: tell the PC now so it stops sending.
                    return failIncoming(channel, state, FileAckStatus.CANCELLED)
                }
                state.nextIndex++
                state.bytesWritten += bytes.size
                report(TransferProgress(chunk.transferId, state.safeName, TransferDirection.RECEIVING,
                    chunksDone = state.nextIndex, totalChunks = state.meta.totalChunks))
            }
            MessageType.FILE_END -> {
                val end = message.payloadAs<FileEndPayload>()
                val state = incoming.remove(end.transferId) ?: return null
                val finalFile = try {
                    val actualHash = withContext(Dispatchers.IO) {
                        state.stream.close()
                        FileHasher.sha256HexOfFile(state.temp.path)
                    }
                    if (state.bytesWritten != state.meta.size || !actualHash.equals(state.meta.sha256, ignoreCase = true)) {
                        state.temp.delete()
                        channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(end.transferId, FileAckStatus.HASH_MISMATCH)))
                        return report(finished(state, TransferStatus.FAILED))
                    }
                    withContext(Dispatchers.IO) {
                        SafeFileName.uniqueFile(downloadDir, state.safeName).also { target ->
                            if (!state.temp.renameTo(target)) throw IOException("Couldn't save ${state.safeName}")
                        }
                    }
                } catch (e: IOException) {
                    // Couldn't finish storing it: the PC must not believe it arrived.
                    state.temp.delete()
                    channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(end.transferId, FileAckStatus.CANCELLED)))
                    return report(finished(state, TransferStatus.FAILED))
                }
                channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(end.transferId, FileAckStatus.OK)))
                report(finished(state, TransferStatus.COMPLETED).copy(savedFile = finalFile))
            }
            MessageType.FILE_CANCEL -> {
                val cancel = message.payloadAs<FileCancelPayload>()
                outgoing[cancel.transferId]?.let { o -> // the PC may be cancelling something we're sending…
                    synchronized(o) { if (!o.finished) o.peerVerdict = FileAckStatus.CANCELLED }
                }
                val state = incoming.remove(cancel.transferId) ?: return null // …or something it was sending us
                withContext(Dispatchers.IO) { runCatching { state.stream.close() }; state.temp.delete() }
                report(finished(state, TransferStatus.CANCELLED))
            }
            MessageType.FILE_ACK -> {
                val ack = message.payloadAs<FileAckPayload>()
                val o = outgoing[ack.transferId] ?: return null
                if (ack.status == FileAckStatus.OK) {
                    outgoing.remove(ack.transferId)
                    return o.progress
                }
                synchronized(o) {
                    o.peerVerdict = ack.status
                    if (!o.finished) return null // still sending: it stops at the next chunk and reports the verdict
                    o.progress = o.progress.copy(status = statusFor(ack.status))
                }
                outgoing.remove(ack.transferId)
                report(o.progress)
            }
            else -> null
        }
    }

    /** Call when the session ends: drops partially received files and stops outgoing sends. */
    fun abortAll() {
        for (state in incoming.values) {
            runCatching { state.stream.close() }
            state.temp.delete()
            report(finished(state, TransferStatus.FAILED))
        }
        incoming.clear()
        for ((id, o) in outgoing) {
            synchronized(o) { if (!o.finished) o.peerVerdict = FileAckStatus.CANCELLED }
            if (o.progress.status == TransferStatus.COMPLETED) outgoing.remove(id) // sent; its ack was lost
        }
    }

    private suspend fun failIncoming(channel: EncryptedChannel, state: Incoming, status: String = FileAckStatus.HASH_MISMATCH): TransferProgress {
        incoming.remove(state.meta.transferId)
        withContext(Dispatchers.IO) { runCatching { state.stream.close() }; state.temp.delete() }
        channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(state.meta.transferId, status)))
        return report(finished(state, TransferStatus.FAILED))
    }

    private fun finished(state: Incoming, status: TransferStatus) = TransferProgress(
        state.meta.transferId, state.safeName, TransferDirection.RECEIVING, status, state.nextIndex, state.meta.totalChunks)

    private fun report(progress: TransferProgress): TransferProgress = progress.also(onProgress)

    private fun readFully(input: java.io.InputStream, buffer: ByteArray): Int {
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
