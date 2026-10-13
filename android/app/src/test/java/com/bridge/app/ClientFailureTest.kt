package com.bridge.app

import com.bridge.app.network.*
import com.bridge.app.protocol.*
import java.io.File
import java.nio.file.Files
import kotlinx.coroutines.*
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phone-side failure paths, against [TestPc] (built from the spec, not the Windows code). */
class ClientFailureTest {
    private object NoStore : PairedDeviceStore {
        override suspend fun save(device: PairedDevice) {}
        override suspend fun list() = emptyList<PairedDevice>()
        override suspend fun revoke(deviceId: String) {}
    }

    private object NoClipboard : ClipboardAccess {
        override fun readText(): String? = null
        override fun writeText(text: String) {}
        override fun setChangeListener(listener: (String) -> Unit) {}
    }

    private fun qrFor(pc: TestPc) = BridgeJson.encodeToString(PairingQrPayload.serializer(),
        PairingQrPayload("token", "127.0.0.1", pc.port, pc.identity.deviceId, "Test PC", pc.publicKeyBase64()))

    @Test
    fun pairing_timesOut_whenPcAcceptsButNeverAnswers() = runBlocking {
        TestPc { stayMute() }.use { pc ->
            val started = System.currentTimeMillis()
            val result = PairingClient(SoftwareIdentity(), NoStore, timeoutMs = 1_000).pairFromQrContent(qrFor(pc), "Phone")
            val elapsed = System.currentTimeMillis() - started
            assertTrue("should fail", result.isFailure)
            assertTrue("friendly message, got: ${result.exceptionOrNull()?.message}",
                result.exceptionOrNull() is PairingException && result.exceptionOrNull()!!.message!!.contains("didn't respond"))
            assertTrue("took ${elapsed}ms", elapsed < 5_000)
        }
    }

    @Test
    fun pairing_showsFriendlyMessage_whenPcUnreachable() = runBlocking {
        val port = java.net.ServerSocket(0).use { it.localPort } // nothing listening there now
        val qr = BridgeJson.encodeToString(PairingQrPayload.serializer(),
            PairingQrPayload("t", "127.0.0.1", port, "pc", "Office PC", SoftwareIdentity().publicKeyBytes().let { java.util.Base64.getEncoder().encodeToString(it) }))
        val error = PairingClient(SoftwareIdentity(), NoStore).pairFromQrContent(qr, "Phone").exceptionOrNull()
        assertTrue("$error", error is PairingException && error.message!!.startsWith("Couldn't reach Office PC"))
    }

    @Test
    fun session_watchdog_dropsSilentPc() = runBlocking {
        TestPc {
            handshake()
            receive() // capabilities
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES))))
            ignoreRest() // then goes silent (half-open), never answers pings
        }.use { pc ->
            val device = PairedDevice(pc.identity.deviceId, "Test PC", pc.publicKeyBase64(), "127.0.0.1", pc.port)
            val dir = Files.createTempDirectory("bridge").toFile()
            val session = BridgeClientSession(SoftwareIdentity(), device, FileTransferClient {}, ClipboardSyncClient(NoClipboard, this), dir,
                pingIntervalMs = 100, peerTimeoutMs = 700)
            val started = System.currentTimeMillis()
            withTimeout(5_000) { session.run() }
            assertTrue(System.currentTimeMillis() - started < 5_000)
            assertTrue(!session.isConnected)
        }
    }

    @Test
    fun sender_stopsEarly_whenPcRejectsTheFile() = runBlocking {
        TestPc {
            handshake()
            receive()
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES))))
            val start = receive().payloadAs<FileStartPayload>()
            send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(start.transferId, FileAckStatus.CANCELLED))) // e.g. disk full
            ignoreRest()
        }.use { pc ->
            val device = PairedDevice(pc.identity.deviceId, "Test PC", pc.publicKeyBase64(), "127.0.0.1", pc.port)
            val dir = Files.createTempDirectory("bridge").toFile()
            val files = FileTransferClient {}
            val connected = CompletableDeferred<Unit>()
            val session = BridgeClientSession(SoftwareIdentity(), device, files, ClipboardSyncClient(NoClipboard, this), dir,
                listener = object : BridgeClientSession.Listener { override fun onConnected(peerFeatures: List<String>) { connected.complete(Unit) } })
            val job = launch(Dispatchers.IO) { session.run() }
            withTimeout(5_000) { connected.await() }

            val big = File(dir, "big.bin").apply { writeBytes(ByteArray(FileTransferClient.CHUNK_SIZE * 300)) }
            val result = withTimeout(10_000) { session.sendFile(big)!! }
            assertEquals(TransferStatus.CANCELLED, result.status)
            assertTrue("sender kept going: ${result.chunksDone}", result.chunksDone < 300)
            session.close()
            job.cancelAndJoin()
        }
    }

    @Test
    fun session_refusesPcThatCannotProveItsKey() = runBlocking {
        TestPc { handshake() }.use { pc ->
            // Pairing stored a different key for this address (e.g. another machine took the IP).
            val device = PairedDevice(pc.identity.deviceId, "Test PC",
                java.util.Base64.getEncoder().encodeToString(SoftwareIdentity().publicKeyBytes()), "127.0.0.1", pc.port)
            val error = runCatching {
                BridgeClientSession(SoftwareIdentity(), device, FileTransferClient {}, ClipboardSyncClient(NoClipboard, this),
                    Files.createTempDirectory("b").toFile()).run()
            }.exceptionOrNull()
            assertTrue("$error", error is AuthenticationException)
        }
    }

    @Test
    fun messagesSentFromOnConnected_areDelivered() = runBlocking {
        // Regression: onConnected used to fire before the channel was published,
        // so a send from the callback (e.g. a share right as it connects) was dropped.
        val got = CompletableDeferred<String>()
        TestPc {
            handshake()
            receive()
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.NOTIFICATIONS))))
            got.complete(receive().type)
            ignoreRest()
        }.use { pc ->
            val device = PairedDevice(pc.identity.deviceId, "Test PC", pc.publicKeyBase64(), "127.0.0.1", pc.port)
            lateinit var session: BridgeClientSession
            var sentOk = false
            session = BridgeClientSession(SoftwareIdentity(), device, FileTransferClient {}, ClipboardSyncClient(NoClipboard, this),
                Files.createTempDirectory("b").toFile(),
                listener = object : BridgeClientSession.Listener {
                    override fun onConnected(peerFeatures: List<String>) {
                        sentOk = runBlocking { session.send(BridgeMessage.create(MessageType.NOTIFICATION_POST, NotificationPostPayload("a", "b", "c", "k"))) }
                    }
                })
            val job = launch(Dispatchers.IO) { session.run() }
            assertEquals(MessageType.NOTIFICATION_POST, withTimeout(5_000) { got.await() })
            assertTrue(sentOk)
            session.close()
            job.cancelAndJoin()
        }
    }
}
