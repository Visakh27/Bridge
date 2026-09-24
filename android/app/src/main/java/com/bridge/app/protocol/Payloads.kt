package com.bridge.app.protocol

import kotlinx.serialization.Serializable

@Serializable data class CapabilitiesPayload(val features: List<String>)

@Serializable data class DeviceStatusPayload(val batteryPercent: Int, val charging: Boolean, val connectionQuality: String)

@Serializable data class ClipboardUpdatePayload(val format: String, val data: String)

@Serializable data class FileStartPayload(val transferId: String, val fileName: String, val size: Long, val sha256: String, val totalChunks: Int)

@Serializable data class FileChunkPayload(val transferId: String, val index: Int, val data: String)

@Serializable data class FileEndPayload(val transferId: String)

@Serializable data class FileAckPayload(val transferId: String, val status: String) // ok | hash_mismatch | cancelled

@Serializable data class FileCancelPayload(val transferId: String)

@Serializable data class NotificationPostPayload(val appName: String, val title: String, val text: String, val key: String)

@Serializable data class NotificationDismissPayload(val key: String)

@Serializable data class ErrorPayload(val code: String, val message: String)

@Serializable data class PairingRequestPayload(val pairingToken: String, val deviceName: String, val publicKeyBase64: String)

@Serializable data class PairingAcceptPayload(val deviceName: String, val publicKeyBase64: String)

@Serializable data class PairingRejectPayload(val reason: String)

@Serializable data class PairingQrPayload(val pairingToken: String, val host: String, val port: Int, val deviceId: String, val deviceName: String)
