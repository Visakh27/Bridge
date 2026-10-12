package com.bridge.app

import com.bridge.app.protocol.BridgeMessage
import com.bridge.app.security.SessionCrypto
import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.ServerSocket
import java.net.Socket
import java.nio.ByteBuffer
import kotlin.concurrent.thread

/**
 * A minimal PC written straight from protocol/protocol.md (independent of the
 * Windows code), for exercising the phone's failure paths on the JVM.
 * [script] runs on a background thread once a phone connects.
 */
class TestPc(private val script: TestPc.Connection.() -> Unit) : AutoCloseable {
    val identity = SoftwareIdentity("test-pc")
    private val server = ServerSocket(0)
    val port: Int get() = server.localPort
    @Volatile var failure: Throwable? = null

    init {
        thread(isDaemon = true) {
            try {
                server.accept().use { script(Connection(it)) }
            } catch (t: Throwable) {
                if (!server.isClosed) failure = t
            }
        }
    }

    inner class Connection(private val socket: Socket) {
        private val input = DataInputStream(socket.getInputStream())
        private val output = DataOutputStream(socket.getOutputStream())
        private val crypto = SessionCrypto()

        private fun readFrame(): ByteArray = ByteArray(input.readInt()).also { input.readFully(it) }
        private fun writeFrame(b: ByteArray) { output.writeInt(b.size); output.write(b); output.flush() }
        private fun field(b: ByteArray) = ByteBuffer.allocate(4 + b.size).putInt(b.size).put(b).array()
        private fun fields(frame: ByteArray): List<ByteArray> {
            val out = mutableListOf<ByteArray>(); var o = 0
            while (o < frame.size) { val n = ByteBuffer.wrap(frame, o, 4).int; out += frame.copyOfRange(o + 4, o + 4 + n); o += 4 + n }
            return out
        }

        /** Accepts the TCP connection but never answers the handshake. */
        fun stayMute() { Thread.sleep(30_000) }

        /** Responder side of the handshake; returns the phone's device id. */
        fun handshake(): String {
            val (phoneId, phoneEph) = fields(readFrame())
            val myEph = crypto.ephemeralPublicKey
            val transcript = SessionCrypto.transcript(String(phoneId), phoneEph, identity.deviceId, myEph)
            writeFrame(field(identity.deviceId.toByteArray()) + field(myEph) + field(identity.sign(transcript)))
            val (phoneSig) = fields(readFrame())
            check(phoneSig.isNotEmpty())
            crypto.completeHandshake(phoneEph, transcript, isInitiator = false)
            return String(phoneId)
        }

        fun receive(): BridgeMessage = BridgeMessage.fromJson(String(crypto.decrypt(readFrame())))
        fun send(message: BridgeMessage) = writeFrame(crypto.encrypt(message.toJson().toByteArray()))
        fun close() = socket.close()
        fun ignoreRest() { try { while (true) readFrame() } catch (e: Exception) { } }
    }

    override fun close() = server.close()

    fun publicKeyBase64(): String = java.util.Base64.getEncoder().encodeToString(identity.publicKeyBytes())
}
