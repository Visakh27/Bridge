package com.bridge.app.protocol

import kotlinx.serialization.Serializable

@Serializable data class CapabilitiesPayload(val features: List<String>)

@Serializable data class DeviceStatusPayload(val batteryPercent: Int, val charging: Boolean, val connectionQuality: String)

@Serializable data class ClipboardUpdatePayload(val format: String, val data: String)

/**
 * Announces a file. Optional fields are omitted when null: [resumable] — the
 * sender waits for file.resume before chunks (peer advertised files.resume);
 * [relativePath] — the file's folder inside a folder transfer ('/'-separated,
 * no file name); [batchId]/[batchIndex]/[batchCount] — groups one folder transfer.
 */
@Serializable data class FileStartPayload(
    val transferId: String,
    val fileName: String,
    val size: Long,
    val sha256: String,
    val totalChunks: Int,
    val resumable: Boolean? = null,
    val relativePath: String? = null,
    val batchId: String? = null,
    val batchIndex: Int? = null,
    val batchCount: Int? = null,
)

/** Receiver → sender: continue from chunk [nextIndex] (0 = from the start). */
@Serializable data class FileResumePayload(val transferId: String, val nextIndex: Int)

@Serializable data class FileChunkPayload(val transferId: String, val index: Int, val data: String)

@Serializable data class FileEndPayload(val transferId: String)

@Serializable data class FileAckPayload(val transferId: String, val status: String) // see FileAckStatus

@Serializable data class FileCancelPayload(val transferId: String)

/**
 * A phone notification. Optional fields are omitted when null (sent with
 * notifications.actions): [appId] — package name, for muting; [actions] — up
 * to 3 buttons the PC can press; [canDismiss] — the PC may clear it here.
 */
@Serializable data class NotificationPostPayload(
    val appName: String,
    val title: String,
    val text: String,
    val key: String,
    val appId: String? = null,
    val actions: List<NotificationActionInfo>? = null,
    val canDismiss: Boolean? = null,
)

/** A notification button; [reply] = true when it takes typed text. */
@Serializable data class NotificationActionInfo(val id: String, val title: String, val reply: Boolean? = null)

/** Phone → PC: the notification is gone. PC → phone (notifications.actions): clear it here. */
@Serializable data class NotificationDismissPayload(val key: String)

/** PC → phone: press button [actionId] of notification [key]; [replyText] only for reply buttons. */
@Serializable data class NotificationActionPayload(val requestId: String, val key: String, val actionId: String, val replyText: String? = null)

/** Phone → PC: outcome of a notification.action (see [NotificationActionStatus]). */
@Serializable data class NotificationActionResultPayload(val requestId: String, val status: String)

/** PC → phone: stop mirroring this app's notifications (undo on the phone). */
@Serializable data class NotificationMutePayload(val appId: String)

object NotificationActionStatus {
    const val OK = "ok"
    /** The notification, or that button, is no longer on the phone. */
    const val GONE = "gone"
    /** Bridge doesn't mirror that app/notification, so it won't act on it. */
    const val REFUSED = "refused"
    /** Malformed request (reply text missing/too long, text for a non-reply button…). */
    const val INVALID = "invalid"
    /** The app didn't accept it. */
    const val FAILED = "failed"
}

@Serializable data class ErrorPayload(val code: String, val message: String)

@Serializable data class PairingRequestPayload(val pairingToken: String, val deviceName: String, val publicKeyBase64: String)

@Serializable data class PairingAcceptPayload(val deviceName: String, val publicKeyBase64: String)

@Serializable data class PairingRejectPayload(val reason: String)

/**
 * Contents of the QR code the PC shows. [publicKeyBase64] is the PC's
 * long-term identity key (SubjectPublicKeyInfo): the phone checks the PC's
 * handshake signature against it before sending the pairing token.
 */
@Serializable data class PairingQrPayload(
    val pairingToken: String,
    val host: String,
    val port: Int,
    val deviceId: String,
    val deviceName: String,
    val publicKeyBase64: String,
)

object FileAckStatus {
    const val OK = "ok"
    const val HASH_MISMATCH = "hash_mismatch"
    const val CANCELLED = "cancelled"
}

object Features {
    const val FILES = "files"
    const val CLIPBOARD = "clipboard"
    const val NOTIFICATIONS = "notifications"
    const val PHOTOS = "photos"
    /** Interrupted transfers continue where they stopped (file.resume). */
    const val FILES_RESUME = "files.resume"
    /** Folder transfers (file.start relativePath / batch fields). */
    const val FILES_FOLDERS = "files.folders"
    /** Reply to, press buttons on, dismiss and mute phone notifications from the PC. */
    const val NOTIFICATIONS_ACTIONS = "notifications.actions"
}

object ErrorCodes {
    const val CONNECTION_TIMEOUT = "CONNECTION_TIMEOUT"
    const val AUTH_FAILED = "AUTH_FAILED"
    const val DEVICE_UNPAIRED = "DEVICE_UNPAIRED"
    const val TRANSFER_INTERRUPTED = "TRANSFER_INTERRUPTED"
    const val PERMISSION_DENIED = "PERMISSION_DENIED"
}
