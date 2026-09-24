package com.bridge.app.protocol

/** Mirrors Bridge.Protocol.MessageType on the Windows side. See protocol/protocol.md. */
object MessageType {
    const val PAIRING_REQUEST = "pairing.request"
    const val PAIRING_ACCEPT = "pairing.accept"
    const val PAIRING_REJECT = "pairing.reject"
    const val CAPABILITIES_ADVERTISE = "capabilities.advertise"
    const val PRESENCE_PING = "presence.ping"
    const val PRESENCE_PONG = "presence.pong"
    const val DEVICE_STATUS = "device.status"
    const val CLIPBOARD_UPDATE = "clipboard.update"
    const val FILE_START = "file.start"
    const val FILE_CHUNK = "file.chunk"
    const val FILE_END = "file.end"
    const val FILE_ACK = "file.ack"
    const val FILE_CANCEL = "file.cancel"
    const val NOTIFICATION_POST = "notification.post"
    const val NOTIFICATION_DISMISS = "notification.dismiss"
    const val ERROR = "error"
}
