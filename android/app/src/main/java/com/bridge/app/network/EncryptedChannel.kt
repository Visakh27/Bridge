package com.bridge.app.network

import com.bridge.app.protocol.BridgeMessage
import com.bridge.app.security.SessionCrypto
import java.nio.ByteBuffer
import java.nio.charset.StandardCharsets

/**
 * Handshake + message-level send/receive over a TcpTransport, matching
 * Bridge.Network.EncryptedChannel on Windows (see protocol/protocol.md).
 */
class EncryptedChannel private constructor(
    private val transport: TcpTransport,
    private val crypto: SessionCrypto,
) {
    val isConnected: Boolean get() = transport.isConnected

    suspend fun send(message: BridgeMessage) {
        val plaintext = message.toJson().toByteArray(StandardCharsets.UTF_8)
        transport.sendFrame(crypto.encrypt(plaintext))
    }

    suspend fun receive(): BridgeMessage {
        val frame = transport.receiveFrame()
        val plaintext = crypto.decrypt(frame)
        return BridgeMessage.fromJson(String(plaintext, StandardCharsets.UTF_8))
    }

    fun close() = transport.close()

    companion object {
        /** Android is always the initiator: it scans the QR and opens the TCP connection to the PC. */
        suspend fun handshakeAsInitiator(transport: TcpTransport, deviceId: String): EncryptedChannel {
            val crypto = SessionCrypto()
            transport.sendFrame(buildHello(deviceId, crypto.ephemeralPublicKey))
            val ack = transport.receiveFrame()
            val (_, peerPub) = parseHello(ack)
            crypto.completeHandshake(peerPub, isInitiator = true)
            return EncryptedChannel(transport, crypto)
        }

        private fun buildHello(deviceId: String, ephemeralPub: ByteArray): ByteArray {
            val idBytes = deviceId.toByteArray(StandardCharsets.UTF_8)
            val header = ByteBuffer.allocate(4).putInt(idBytes.size).array()
            return header + idBytes + ephemeralPub
        }

        private fun parseHello(frame: ByteArray): Pair<String, ByteArray> {
            val idLen = ByteBuffer.wrap(frame, 0, 4).int
            val deviceId = String(frame, 4, idLen, StandardCharsets.UTF_8)
            val pub = frame.copyOfRange(4 + idLen, frame.size)
            return deviceId to pub
        }
    }
}
