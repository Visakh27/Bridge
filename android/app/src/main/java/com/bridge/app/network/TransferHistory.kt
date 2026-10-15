package com.bridge.app.network

import java.io.File

/** One row of "Recent transfers". Mirrors Bridge.Core.TransferEntry. */
data class TransferEntry(
    val transferId: String,
    val fileName: String,
    val direction: TransferDirection,
    val status: TransferStatus,
    val fraction: Double,
    val updated: Long,
    val savedFile: File? = null,
    val sourceFile: File? = null,
    /** The source is Bridge's private copy (deleted when the row is cleared). */
    val retainedCopy: Boolean = false,
    val folder: String? = null,
) {
    /** A single-file send that failed or was refused, whose file is still there, can be sent again. */
    val canRetry: Boolean
        get() = direction == TransferDirection.SENDING && folder == null &&
            (status == TransferStatus.FAILED || status == TransferStatus.CANCELLED) && sourceFile?.exists() == true

    /** A received file that is still there can be opened. */
    val canOpen: Boolean
        get() = direction == TransferDirection.RECEIVING && status == TransferStatus.COMPLETED && savedFile?.exists() == true
}

/**
 * The last [capacity] transfers, newest first, fed from FileTransferClient's
 * progress callback; a transfer's updates replace its row. Thread-safe.
 */
class TransferHistory(private val capacity: Int = 20, private val now: () -> Long = System::currentTimeMillis) {
    private val lock = Any()
    private val rows = ArrayList<TransferEntry>()

    val entries: List<TransferEntry> get() = synchronized(lock) { rows.toList() }

    fun update(p: TransferProgress) = synchronized(lock) {
        val i = rows.indexOfFirst { it.transferId == p.transferId && it.direction == p.direction }
        val old = if (i >= 0) rows.removeAt(i) else null
        rows.add(0, TransferEntry(p.transferId, p.fileName, p.direction, p.status, p.fraction, now(),
            p.savedFile ?: old?.savedFile, p.sourceFile ?: old?.sourceFile, p.retainedCopy, p.relativePath?.substringBefore('/')))
        while (rows.size > capacity) rows.removeAt(rows.size - 1)
    }

    /** Drops a row (e.g. it was retried, so a new row takes over). */
    fun remove(transferId: String) = synchronized(lock) { rows.removeAll { it.transferId == transferId } }

    /** Removes finished rows; returns private copies they kept, for the caller to delete. */
    fun clearFinished(): List<File> = synchronized(lock) {
        val done = rows.filter { it.status != TransferStatus.IN_PROGRESS && it.status != TransferStatus.INTERRUPTED }
        rows.removeAll(done.toSet())
        done.filter { it.retainedCopy }.mapNotNull { it.sourceFile }
    }

    companion object {
        fun describe(e: TransferEntry): String {
            val percent = (e.fraction * 100).toInt()
            return when (e.status) {
                TransferStatus.IN_PROGRESS -> "${if (e.direction == TransferDirection.SENDING) "Sending" else "Receiving"} · $percent%"
                TransferStatus.INTERRUPTED -> "Paused at $percent% · continues when reconnected"
                TransferStatus.COMPLETED -> if (e.direction == TransferDirection.SENDING) "Sent" else "Received"
                TransferStatus.CANCELLED -> "Cancelled"
                TransferStatus.FAILED -> "Didn't transfer correctly"
            }
        }
    }
}
