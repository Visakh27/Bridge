package com.bridge.app

import com.bridge.app.network.*
import com.bridge.app.protocol.*
import com.bridge.app.security.FileHasher
import java.io.File
import java.nio.file.Files
import java.util.Base64
import kotlinx.coroutines.*
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Review pass 3: phone-side failure paths and hostile input. */
class RobustnessTest {
    private class ThrowingClipboard : ClipboardAccess {
        @Volatile var attempts = 0
        override fun readText(): String? = null
        override fun writeText(text: String) { attempts++; throw IllegalStateException("clipboard unavailable") }
        override fun setChangeListener(listener: (String) -> Unit) {}
    }

    private fun device(pc: TestPc) = PairedDevice(pc.identity.deviceId, "Test PC", pc.publicKeyBase64(), "127.0.0.1", pc.port)

    @Test
    fun safeFileName_fitsFilesystemByteLimit() {
        // ext4/f2fs allow 255 *bytes* per name; 200 CJK chars is 600 bytes.
        val name = SafeFileName.sanitize("写".repeat(200) + ".jpg")
        assertTrue("${name.toByteArray().size} bytes", name.toByteArray(Charsets.UTF_8).size <= 200)
        assertTrue(name.endsWith(".jpg"))
        val dir = Files.createTempDirectory("b").toFile()
        assertTrue(File(dir, name).createNewFile())
        assertTrue(File(dir, SafeFileName.uniqueFile(dir, name).name).createNewFile())
        dir.deleteRecursively()
    }

    @Test
    fun safeFileName_stripsBidiOverrides() {
        val name = SafeFileName.sanitize("photo‮gnp.apk")
        assertFalse(name.contains('‮'))
        assertTrue(name.endsWith(".apk"))
    }

    @Test
    fun longUnicodeName_isReceivedNotLost() = runBlocking {
        val ackStatus = CompletableDeferred<String>()
        val payload = ByteArray(1000) { it.toByte() }
        TestPc {
            handshake(); receive()
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES))))
            send(BridgeMessage.create(MessageType.FILE_START, FileStartPayload("t1", "写".repeat(200) + ".jpg", 1000, FileHasher.sha256Hex(payload), 1)))
            send(BridgeMessage.create(MessageType.FILE_CHUNK, FileChunkPayload("t1", 0, Base64.getEncoder().encodeToString(payload))))
            send(BridgeMessage.create(MessageType.FILE_END, FileEndPayload("t1")))
            while (true) {
                val m = receive()
                if (m.type == MessageType.FILE_ACK) { ackStatus.complete(m.payloadAs<FileAckPayload>().status); break }
            }
            ignoreRest()
        }.use { pc ->
            val dir = Files.createTempDirectory("b").toFile()
            val job = launch(Dispatchers.IO) {
                runCatching { BridgeClientSession(SoftwareIdentity(), device(pc), FileTransferClient {}, ClipboardSyncClient(ThrowingClipboard(), this), dir).run() }
            }
            assertEquals(FileAckStatus.OK, withTimeout(5_000) { ackStatus.await() })
            assertEquals(1, dir.listFiles()!!.count { !it.name.endsWith(".part") })
            assertEquals(0, dir.listFiles()!!.count { it.name.endsWith(".part") })
            job.cancelAndJoin()
        }
    }

    @Test
    fun failingHandler_doesNotDropTheSession() = runBlocking {
        val pongs = CompletableDeferred<Unit>()
        val clipboard = ThrowingClipboard()
        TestPc {
            handshake(); receive()
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.CLIPBOARD))))
            send(BridgeMessage.create(MessageType.CLIPBOARD_UPDATE, ClipboardUpdatePayload("text", "x")))
            send(BridgeMessage.createEmpty(MessageType.PRESENCE_PING))
            while (true) { if (receive().type == MessageType.PRESENCE_PONG) { pongs.complete(Unit); break } }
            ignoreRest()
        }.use { pc ->
            val sync = ClipboardSyncClient(clipboard, this).apply { enabled = true }
            val session = BridgeClientSession(SoftwareIdentity(), device(pc), FileTransferClient {}, sync, Files.createTempDirectory("b").toFile())
            val job = launch(Dispatchers.IO) { runCatching { session.run() } }
            withTimeout(5_000) { pongs.await() }
            assertTrue(clipboard.attempts > 0)
            assertTrue(session.isConnected)
            session.close(); job.cancelAndJoin()
        }
    }

    @Test
    fun maliciousServer_cannotMakePhoneBufferHugeHandshakeFrame() = runBlocking {
        val server = java.net.ServerSocket(0)
        val accepted = thread {
            server.accept().use { s ->
                java.io.DataInputStream(s.getInputStream()).let { val n = it.readInt(); it.readFully(ByteArray(n)) }
                java.io.DataOutputStream(s.getOutputStream()).writeInt(16 * 1024 * 1024) // then sends nothing
                Thread.sleep(5_000)
            }
        }
        val started = System.currentTimeMillis()
        val result = runCatching {
            EncryptedChannel.handshakeAsInitiator(TcpTransport.connect("127.0.0.1", server.localPort), SoftwareIdentity(), SoftwareIdentity().publicKeyBytes())
        }
        assertTrue(result.isFailure)
        assertTrue("waited ${System.currentTimeMillis() - started} ms for a 16 MB HELLO_ACK", System.currentTimeMillis() - started < 2_000)
        server.close(); accepted.join()
    }

    private fun thread(block: () -> Unit) = kotlin.concurrent.thread(isDaemon = true) { runCatching(block) }
}
