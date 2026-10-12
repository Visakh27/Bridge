package com.bridge.app.network

import com.bridge.app.protocol.BridgeMessage
import com.bridge.app.security.IdentityKey
import com.bridge.app.security.IdentityKeys
import com.bridge.app.security.SessionCrypto
import java.io.IOException
import java.nio.ByteBuffer
import java.security.GeneralSecurityException
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock

/** The peer couldn't prove it holds the expected identity key. */
class AuthenticationException(message: String) : GeneralSecurityException(message)

/**
 * Authenticated handshake + message-level send/receive over a TcpTransport,
 * matching Bridge.Network.EncryptedChannel on Windows (protocol/protocol.md):
 *
 *   HELLO     phone → PC: [len][deviceId][len][ephemeralKey]
 *   HELLO_ACK PC → phone: [len][deviceId][len][ephemeralKey][len][signature]
 *   AUTH      phone → PC: [len][signature]
 *
 * All lengths are 4-byte big-endian. Signatures are ECDSA-P256-SHA256 (DER)
 * over [SessionCrypto.transcript]. The phone checks the PC's signature against
 * the key from the QR code (or the stored pairing) before sending anything else.
 */
class EncryptedChannel private constructor(
    private val transport: TcpTransport,
    private val crypto: SessionCrypto,
    /** The PC's device id from its (verified) HELLO_ACK. */
    val peerDeviceId: String,
) {
    private val sendLock = Mutex()

    val isConnected: Boolean get() = transport.isConnected

    suspend fun send(message: BridgeMessage) {
        val plaintext = message.toJson().toByteArray(Charsets.UTF_8)
        // Encrypt inside the lock: nonces are sequence numbers, so frames must
        // reach the wire in the order they were encrypted.
        sendLock.withLock { transport.sendFrame(crypto.encrypt(plaintext)) }
    }

    /** Single reader only. Throws on transport, integrity or JSON errors. */
    suspend fun receive(): BridgeMessage {
        val frame = transport.receiveFrame()
        val plaintext = crypto.decrypt(frame)
        return BridgeMessage.fromJson(String(plaintext, Charsets.UTF_8))
    }

    fun close() = transport.close()

    companion object {
        private const val MAX_ID_LENGTH = 256
        private const val MAX_KEY_LENGTH = 1024
        private const val MAX_SIGNATURE_LENGTH = 256

        /** Android is always the initiator: it scans the QR and opens the TCP connection to the PC. */
        suspend fun handshakeAsInitiator(transport: TcpTransport, identity: IdentityKey, expectedPeerKey: ByteArray): EncryptedChannel {
            try {
                val crypto = SessionCrypto()
                val myEphemeral = crypto.ephemeralPublicKey
                transport.sendFrame(field(identity.deviceId.toByteArray(Charsets.UTF_8)) + field(myEphemeral))

                val reader = FrameReader(transport.receiveFrame())
                val peerId = String(reader.next(MAX_ID_LENGTH), Charsets.UTF_8)
                val peerEphemeral = reader.next(MAX_KEY_LENGTH)
                val peerSig = reader.next(MAX_SIGNATURE_LENGTH)
                reader.end()

                val transcript = SessionCrypto.transcript(identity.deviceId, myEphemeral, peerId, peerEphemeral)
                if (!IdentityKeys.verify(expectedPeerKey, transcript, peerSig)) {
                    throw AuthenticationException("PC failed to prove its identity")
                }

                transport.sendFrame(field(identity.sign(transcript)))
                crypto.completeHandshake(peerEphemeral, transcript, isInitiator = true)
                return EncryptedChannel(transport, crypto, peerId)
            } catch (e: Throwable) {
                transport.close()
                throw e
            }
        }

        private fun field(value: ByteArray): ByteArray =
            ByteBuffer.allocate(4 + value.size).putInt(value.size).put(value).array()
    }

    private class FrameReader(private val frame: ByteArray) {
        private var offset = 0

        fun next(maxLength: Int): ByteArray {
            if (frame.size - offset < 4) throw IOException("Truncated handshake frame")
            val len = ByteBuffer.wrap(frame, offset, 4).int
            offset += 4
            if (len < 0 || len > maxLength || len > frame.size - offset) throw IOException("Invalid field length in handshake frame")
            return frame.copyOfRange(offset, offset + len).also { offset += len }
        }

        fun end() {
            if (offset != frame.size) throw IOException("Trailing bytes in handshake frame")
        }
    }
}
