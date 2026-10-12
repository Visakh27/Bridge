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

    private val cancelledTransfers = ConcurrentHashMap.newKeySet<String>()
    private val awaitingAck = ConcurrentHashMap<String, TransferProgress>()
    private val incoming = mutableMapOf<String, Incoming>() // only touched by the session's receive loop

    /** Sends a file; returns the final status instead of throwing for I/O problems or cancellation. */
    suspend fun sendFile(channel: EncryptedChannel, file: File, displayName: String = file.name): TransferProgress {
        val transferId = UUID.randomUUID().toString()
        var progress = TransferProgress(transferId, displayName, TransferDirection.SENDING)
        var started = false
        try {
            val size = file.length()
            val hash = withContext(Dispatchers.IO) { FileHasher.sha256HexOfFile(file.path) }
            val totalChunks = ((size + CHUNK_SIZE - 1) / CHUNK_SIZE).toInt()
            progress = progress.copy(totalChunks = totalChunks)
            onProgress(progress)

            channel.send(BridgeMessage.create(MessageType.FILE_START,
                FileStartPayload(transferId, displayName, size, hash, totalChunks)))
            started = true

            withContext(Dispatchers.IO) { file.inputStream().buffered(CHUNK_SIZE) }.use { input ->
                val buffer = ByteArray(CHUNK_SIZE)
                for (index in 0 until totalChunks) {
                    if (transferId in cancelledTransfers) throw CancellationException("Cancelled by user")
                    val read = withContext(Dispatchers.IO) { readFully(input, buffer) }
                    if (read == 0) throw IOException("File shrank while it was being sent")
                    val chunkB64 = Base64.getEncoder().encodeToString(buffer.copyOf(read))
                    channel.send(BridgeMessage.create(MessageType.FILE_CHUNK, FileChunkPayload(transferId, index, chunkB64)))
                    progress = progress.copy(chunksDone = index + 1)
                    onProgress(progress)
                }
            }

            progress = progress.copy(status = TransferStatus.COMPLETED)
            awaitingAck[transferId] = progress
            channel.send(BridgeMessage.create(MessageType.FILE_END, FileEndPayload(transferId)))
        } catch (e: CancellationException) {
            progress = progress.copy(status = TransferStatus.CANCELLED)
            if (started) trySendCancel(channel, transferId)
            if (transferId !in cancelledTransfers) { onProgress(progress); throw e } // coroutine itself cancelled
        } catch (e: IOException) {
            progress = progress.copy(status = TransferStatus.FAILED)
            if (started) trySendCancel(channel, transferId)
        } finally {
            cancelledTransfers.remove(transferId)
            if (progress.status != TransferStatus.COMPLETED) awaitingAck.remove(transferId)
        }
        onProgress(progress)
        return progress
    }

    fun cancelTransfer(transferId: String) { cancelledTransfers.add(transferId) }

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
                val state = withContext(Dispatchers.IO) {
                    downloadDir.mkdirs()
                    val temp = File(downloadDir, ".bridge-${UUID.randomUUID()}.part")
                    Incoming(meta, safeName, temp, FileOutputStream(temp))
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
                withContext(Dispatchers.IO) { state.stream.write(bytes) }
                state.nextIndex++
                state.bytesWritten += bytes.size
                report(TransferProgress(chunk.transferId, state.safeName, TransferDirection.RECEIVING,
                    chunksDone = state.nextIndex, totalChunks = state.meta.totalChunks))
            }
            MessageType.FILE_END -> {
                val end = message.payloadAs<FileEndPayload>()
                val state = incoming.remove(end.transferId) ?: return null
                val actualHash = withContext(Dispatchers.IO) {
                    state.stream.close()
                    FileHasher.sha256HexOfFile(state.temp.path)
                }
                if (state.bytesWritten != state.meta.size || !actualHash.equals(state.meta.sha256, ignoreCase = true)) {
                    state.temp.delete()
                    channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(end.transferId, FileAckStatus.HASH_MISMATCH)))
                    return report(finished(state, TransferStatus.FAILED))
                }
                val finalFile = withContext(Dispatchers.IO) {
                    SafeFileName.uniqueFile(downloadDir, state.safeName).also { target ->
                        if (!state.temp.renameTo(target)) throw IOException("Couldn't save ${state.safeName}")
                    }
                }
                channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(end.transferId, FileAckStatus.OK)))
                report(finished(state, TransferStatus.COMPLETED).copy(savedFile = finalFile))
            }
            MessageType.FILE_CANCEL -> {
                val cancel = message.payloadAs<FileCancelPayload>()
                cancelTransfer(cancel.transferId) // the PC may be cancelling something we're sending…
                val state = incoming.remove(cancel.transferId) ?: return null // …or something it was sending us
                withContext(Dispatchers.IO) { state.stream.close(); state.temp.delete() }
                report(finished(state, TransferStatus.CANCELLED))
            }
            MessageType.FILE_ACK -> {
                val ack = message.payloadAs<FileAckPayload>()
                val sent = awaitingAck.remove(ack.transferId) ?: return null
                if (ack.status == FileAckStatus.OK) sent
                else report(sent.copy(status = if (ack.status == FileAckStatus.CANCELLED) TransferStatus.CANCELLED else TransferStatus.FAILED))
            }
            else -> null
        }
    }

    /** Drops every partially received file (call when the session ends). */
    fun abortIncoming() {
        for (state in incoming.values) {
            runCatching { state.stream.close() }
            state.temp.delete()
            report(finished(state, TransferStatus.FAILED))
        }
        incoming.clear()
    }

    private suspend fun failIncoming(channel: EncryptedChannel, state: Incoming): TransferProgress {
        incoming.remove(state.meta.transferId)
        withContext(Dispatchers.IO) { runCatching { state.stream.close() }; state.temp.delete() }
        channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(state.meta.transferId, FileAckStatus.HASH_MISMATCH)))
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
