package com.bridge.app.network

import com.bridge.app.protocol.*
import com.bridge.app.security.FileHasher
import java.io.File
import java.io.FileOutputStream
import java.util.Base64
import java.util.UUID
import kotlin.math.ceil

enum class TransferDirection { SENDING, RECEIVING }
enum class TransferStatus { IN_PROGRESS, COMPLETED, FAILED, CANCELLED }

data class TransferProgress(
    val transferId: String,
    val fileName: String,
    val direction: TransferDirection,
    var status: TransferStatus = TransferStatus.IN_PROGRESS,
    var chunksDone: Int = 0,
    var totalChunks: Int = 0,
) {
    val fraction: Double get() = if (totalChunks == 0) 0.0 else chunksDone.toDouble() / totalChunks
}

/** Mirrors Bridge.Network.FileTransferService on Windows. Chunk size must match (see protocol/protocol.md). */
class FileTransferClient(private val onProgress: (TransferProgress) -> Unit) {
    companion object { const val CHUNK_SIZE = 64 * 1024 }

    private val cancelledTransfers = mutableSetOf<String>()
    private val incomingStreams = mutableMapOf<String, FileOutputStream>()
    private val incomingTempFiles = mutableMapOf<String, File>()
    private val incomingMeta = mutableMapOf<String, FileStartPayload>()

    suspend fun sendFile(channel: EncryptedChannel, file: File) {
        val transferId = UUID.randomUUID().toString()
        val hash = FileHasher.sha256HexOfFile(file.path)
        val totalChunks = ceil(file.length().toDouble() / CHUNK_SIZE).toInt()

        val progress = TransferProgress(transferId, file.name, TransferDirection.SENDING, totalChunks = totalChunks)
        onProgress(progress)

        channel.send(BridgeMessage.create(MessageType.FILE_START,
            FileStartPayload(transferId, file.name, file.length(), hash, totalChunks)))

        file.inputStream().use { input ->
            val buffer = ByteArray(CHUNK_SIZE)
            var index = 0
            while (true) {
                if (transferId in cancelledTransfers) {
                    progress.status = TransferStatus.CANCELLED
                    onProgress(progress)
                    return
                }
                val read = input.read(buffer)
                if (read <= 0) break
                val chunkB64 = Base64.getEncoder().encodeToString(buffer.copyOf(read))
                channel.send(BridgeMessage.create(MessageType.FILE_CHUNK, FileChunkPayload(transferId, index, chunkB64)))
                index++
                progress.chunksDone = index
                onProgress(progress)
            }
        }

        channel.send(BridgeMessage.create(MessageType.FILE_END, FileEndPayload(transferId)))
        progress.status = TransferStatus.COMPLETED
        onProgress(progress)
    }

    fun cancelTransfer(transferId: String) { cancelledTransfers.add(transferId) }

    /** Feed every incoming BridgeMessage here; writes into downloadDir on completion. */
    suspend fun handleIncoming(channel: EncryptedChannel, message: BridgeMessage, downloadDir: File): TransferProgress? {
        return when (message.type) {
            MessageType.FILE_START -> {
                val meta = message.payloadAs<FileStartPayload>()
                incomingMeta[meta.transferId] = meta
                downloadDir.mkdirs()
                val temp = File(downloadDir, meta.fileName + ".part")
                incomingTempFiles[meta.transferId] = temp
                incomingStreams[meta.transferId] = FileOutputStream(temp)
                TransferProgress(meta.transferId, meta.fileName, TransferDirection.RECEIVING, totalChunks = meta.totalChunks)
                    .also(onProgress)
            }
            MessageType.FILE_CHUNK -> {
                val chunk = message.payloadAs<FileChunkPayload>()
                val stream = incomingStreams[chunk.transferId] ?: return null
                stream.write(Base64.getDecoder().decode(chunk.data))
                val meta = incomingMeta[chunk.transferId] ?: return null
                TransferProgress(chunk.transferId, meta.fileName, TransferDirection.RECEIVING,
                    totalChunks = meta.totalChunks, chunksDone = chunk.index + 1).also(onProgress)
            }
            MessageType.FILE_END -> {
                val end = message.payloadAs<FileEndPayload>()
                val stream = incomingStreams.remove(end.transferId) ?: return null
                stream.close()
                val meta = incomingMeta[end.transferId]!!
                val temp = incomingTempFiles[end.transferId]!!
                val actualHash = FileHasher.sha256HexOfFile(temp.path)

                if (actualHash != meta.sha256) {
                    channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(end.transferId, "hash_mismatch")))
                    temp.delete()
                    return TransferProgress(end.transferId, meta.fileName, TransferDirection.RECEIVING,
                        TransferStatus.FAILED, meta.totalChunks, meta.totalChunks).also(onProgress)
                }

                val finalFile = File(downloadDir, meta.fileName)
                temp.renameTo(finalFile)
                channel.send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(end.transferId, "ok")))
                TransferProgress(end.transferId, meta.fileName, TransferDirection.RECEIVING,
                    TransferStatus.COMPLETED, meta.totalChunks, meta.totalChunks).also(onProgress)
            }
            MessageType.FILE_CANCEL -> {
                val cancel = message.payloadAs<FileCancelPayload>()
                incomingStreams.remove(cancel.transferId)?.close()
                incomingTempFiles.remove(cancel.transferId)?.delete()
                null
            }
            else -> null
        }
    }
}
