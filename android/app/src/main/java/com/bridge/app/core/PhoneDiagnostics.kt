package com.bridge.app.core

import com.bridge.app.network.TransferProgress
import com.bridge.app.network.TransferStatus

/**
 * The phone's diagnostics events, in one place (plain Kotlin, so the
 * cross-platform tests run exactly what the app logs). Only typed
 * [LogField]s: counts, statuses, error types, hashed ids.
 */
object PhoneDiagnostics {
    fun sessionStarted(log: DiagnosticsLog, peerId: String, features: List<String>) =
        log.info("session.started", LogField.id("peer", peerId, log), LogField.num("features", features.size),
            LogField.flag("actions", "notifications.actions" in features))

    fun sessionEnded(log: DiagnosticsLog, peerId: String, durationMs: Long) =
        log.info("session.ended", LogField.id("peer", peerId, log), LogField.ms("duration_ms", durationMs))

    fun connectAttempt(log: DiagnosticsLog, peerId: String) = log.info("connect.attempt", LogField.id("peer", peerId, log))

    fun connectFailed(log: DiagnosticsLog, error: Throwable) = log.warn("connect.failed", LogField.error(error))

    /** Final outcomes only (not every chunk). */
    fun transfer(log: DiagnosticsLog, p: TransferProgress) {
        if (p.status == TransferStatus.IN_PROGRESS) return
        log.info("transfer.done", LogField.id("id", p.transferId, log),
            LogField.code("dir", p.direction.name), LogField.code("status", p.status.name),
            LogField.num("chunks", p.totalChunks), LogField.num("resumed_from", p.resumedFrom),
            LogField.flag("folder", p.batchId != null), LogField.flag("kept_for_retry", p.retainedCopy))
    }
}
