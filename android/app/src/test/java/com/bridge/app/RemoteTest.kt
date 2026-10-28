package com.bridge.app

import com.bridge.app.network.*
import com.bridge.app.protocol.*
import java.io.IOException
import java.net.ServerSocket
import java.nio.file.Files
import java.util.Collections
import kotlin.concurrent.thread
import kotlinx.coroutines.*
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phase I on the phone: reaching the PC through the user's relay. */
class RemoteTest {
    private val key = ByteArray(32) { (it + 1).toByte() }

    // ------------------------------------------------------------------ the join protocol

    @Test
    fun rooms_and_proofs_match_the_pc_byte_for_byte() {
        // Same vectors as Bridge.Tests RemoteTests (also checked against an independent Python implementation).
        val room = RelayProtocol.room("PCKEY".toByteArray(), "PHONEKEY".toByteArray())
        assertEquals("97c559c85be584f787dd62067d66f898b61169f327a8e60e838813fd76135f20", room)
        assertEquals("6c0d263c2159a79e732516c2469caf91b8b1d8ec9782ec1bc667c781f559173b",
            RelayProtocol.proof(key, "phone", room, 1_700_000_000, "00112233445566778899aabbccddeeff"))
        val hello = kotlinx.serialization.json.Json.parseToJsonElement(RelayProtocol.hello(key, "phone", room, 1_700_000_000_123L, "00112233445566778899aabbccddeeff")).toString()
        assertTrue(hello, hello.contains("\"ts\":1700000000") && hello.contains("\"proof\":\"6c0d263c"))
    }

    @Test
    fun relay_config_from_the_pc_is_checked() {
        val k = RelayConfig.encodeKey(key)
        assertEquals(RelayConfig("relay.example.com", 47830, key), RelayConfig.from(RelayConfigPayload(true, "Relay.Example.com", 47830, k)))
        assertNotNull(RelayConfig.from(RelayConfigPayload(true, "203.0.113.7", 1, k)))
        assertNotNull(RelayConfig.from(RelayConfigPayload(true, "2001:db8::7", 443, k)))
        for (bad in listOf(
            RelayConfigPayload(false, "relay.example.com", 47830, k),
            RelayConfigPayload(true, null, 47830, k),
            RelayConfigPayload(true, "relay.example.com", null, k),
            RelayConfigPayload(true, "relay.example.com", 0, k),
            RelayConfigPayload(true, "relay.example.com", 70000, k),
            RelayConfigPayload(true, "relay example.com", 1, k),
            RelayConfigPayload(true, "evil.com/../x", 1, k),
            RelayConfigPayload(true, "relay.example.com", 1, "short"),
            RelayConfigPayload(true, "relay.example.com", 1, "$k="),
            RelayConfigPayload(true, "relay.example.com", 1, null),
        )) assertNull("$bad", RelayConfig.from(bad))
        assertFalse(RelayConfig("h", 1, key).toString().contains(k)) // never logged with its key
    }

    @Test
    fun relay_config_messages_update_the_store_and_garbage_does_not() {
        val store = InMemoryRelayConfigStore()
        val changes = mutableListOf<Boolean>()
        val handler = RelayConfigHandler(store) { _, on -> changes += on }
        fun msg(p: RelayConfigPayload) = BridgeMessage.create(MessageType.RELAY_CONFIG, p)
        assertTrue(handler.handle("pc1", msg(RelayConfigPayload(true, "relay.example.com", 47830, RelayConfig.encodeKey(key)))))
        assertEquals("relay.example.com", store.get("pc1")?.host)
        assertNull(store.get("pc2")) // per PC
        assertFalse(handler.handle("pc1", msg(RelayConfigPayload(true, "bad host", 1, RelayConfig.encodeKey(key)))))
        assertEquals("relay.example.com", store.get("pc1")?.host) // a broken update leaves the old one
        assertTrue(handler.handle("pc1", msg(RelayConfigPayload(false))))
        assertNull(store.get("pc1"))
        assertEquals(listOf(true, false), changes)
    }

    /** A spec-following relay stand-in: answers the hello, then (if paired) echoes bytes back. */
    private class FakeRelay(val key: ByteArray, val answer: (role: String, room: String) -> String) : AutoCloseable {
        private val server = ServerSocket(0)
        val port get() = server.localPort
        val hellos: MutableList<String> = Collections.synchronizedList(mutableListOf())
        init {
            thread(isDaemon = true) {
                runCatching {
                    while (true) {
                        val s = server.accept()
                        thread(isDaemon = true) {
                            s.use {
                                val line = RelayProtocol.readLine(it.getInputStream()) ?: return@thread
                                hellos += line
                                val o = kotlinx.serialization.json.Json.parseToJsonElement(line) as kotlinx.serialization.json.JsonObject
                                fun f(n: String) = (o[n] as kotlinx.serialization.json.JsonPrimitive).content
                                val ok = RelayProtocol.proof(key, f("role"), f("room"), f("ts").toLong(), f("nonce")) == f("proof")
                                val reply = if (!ok) """{"t":"error","code":"unauthorized"}""" else answer(f("role"), f("room"))
                                it.getOutputStream().write("$reply\n".toByteArray()); it.getOutputStream().flush()
                                if (reply.contains("paired")) { val buf = ByteArray(4096); val inp = it.getInputStream(); while (true) { val n = inp.read(buf); if (n < 0) break; it.getOutputStream().write(buf, 0, n); it.getOutputStream().flush() } }
                            }
                        }
                    }
                }
            }
        }
        override fun close() = server.close()
    }

    @Test
    fun the_phone_joins_with_a_valid_proof_and_gets_a_pipe() = runBlocking {
        FakeRelay(key) { _, _ -> """{"t":"paired"}""" }.use { relay ->
            val room = RelayProtocol.room("PCKEY".toByteArray(), "PHONEKEY".toByteArray())
            val t = RelayClient.join(RelayConfig("127.0.0.1", relay.port, key), room)
            t.sendFrame("hello pc".toByteArray())
            assertEquals("hello pc", String(withTimeout(5_000) { t.receiveFrame() })) // the echo comes back framed: it's a plain pipe
            t.close()
            val hello = relay.hellos.single()
            assertTrue(hello.contains("\"role\":\"phone\"") && hello.contains(room))
            assertFalse(hello.contains(RelayConfig.encodeKey(key))) // the key itself never goes to the relay
        }
    }

    @Test
    fun refusals_come_back_as_their_codes() = runBlocking {
        for (code in listOf("pc_offline", "busy")) FakeRelay(key) { _, _ -> """{"t":"error","code":"$code"}""" }.use { relay ->
            val e = runCatching { RelayClient.join(RelayConfig("127.0.0.1", relay.port, key), "a".repeat(64)) }.exceptionOrNull()
            assertEquals(code, (e as? RelayRefusedException)?.code)
        }
        FakeRelay(ByteArray(32)) { _, _ -> """{"t":"paired"}""" }.use { relay -> // the relay has a different key
            val e = runCatching { RelayClient.join(RelayConfig("127.0.0.1", relay.port, key), "a".repeat(64)) }.exceptionOrNull()
            assertEquals("unauthorized", (e as? RelayRefusedException)?.code)
        }
        FakeRelay(key) { _, _ -> """{"t":"error","code":"<script>"}""" }.use { relay -> // nothing odd reaches the UI or log
            val e = runCatching { RelayClient.join(RelayConfig("127.0.0.1", relay.port, key), "a".repeat(64)) }.exceptionOrNull()
            assertEquals("unknown", (e as? RelayRefusedException)?.code)
        }
    }

    // ------------------------------------------------------------------ the reconnect loop

    private fun pc(id: String) = PairedDevice(id, id, "AAAA", "127.0.0.1", 1)

    private class MemStore(vararg initial: PairedDevice) : PairedDeviceStore {
        val devices: MutableList<PairedDevice> = Collections.synchronizedList(initial.toMutableList())
        override suspend fun save(device: PairedDevice) { devices.removeAll { it.deviceId == device.deviceId }; devices.add(device) }
        override suspend fun list() = devices.toList()
        override suspend fun revoke(deviceId: String) { devices.removeAll { it.deviceId == deviceId } }
    }

    private fun handle(run: suspend () -> Unit) = object : SessionHandle {
        override suspend fun run() = run()
        override fun close() {}
    }

    @Test
    fun an_unreachable_pc_is_tried_through_its_relay() = runBlocking {
        val routes = Collections.synchronizedList(mutableListOf<String>())
        val connected = CompletableDeferred<String>()
        val loop = ReconnectLoop(MemStore(pc("home")), null,
            sessionFactory = { handle { routes += "lan"; throw IOException("not on this network") } },
            backoffMs = listOf(60_000), chooser = PcChooser(InMemoryPcPreferences()),
            relaySessionFactory = { p -> handle { routes += "relay"; connected.complete(p.deviceId); awaitCancellation() } })
        val job = launch { loop.run() }
        assertEquals("home", withTimeout(3_000) { connected.await() }) // no 60 s wait in between
        assertEquals(listOf("lan", "relay"), routes.toList())
        job.cancelAndJoin()
    }

    @Test
    fun the_relay_is_not_tried_without_one_or_when_the_pc_answered() = runBlocking {
        var relayTries = 0
        val noRelay = ReconnectLoop(MemStore(pc("a")), null, { handle { throw IOException("x") } }, backoffMs = listOf(150, 60_000),
            relaySessionFactory = { null })
        val j1 = launch { noRelay.run() }
        delay(400); j1.cancelAndJoin()

        // The PC answered (full, or a protocol failure): it's reachable, so the relay wouldn't help.
        val answered = ReconnectLoop(MemStore(pc("b")), null, { handle { throw PcFullException("8 phones") } }, backoffMs = listOf(60_000),
            relaySessionFactory = { handle { relayTries++ } })
        val j2 = launch { answered.run() }
        delay(400); j2.cancelAndJoin()
        assertEquals(0, relayTries)
    }

    @Test
    fun a_pc_that_forgot_this_phone_is_dropped_even_when_reached_through_the_relay() = runBlocking {
        val store = MemStore(pc("old"), pc("other"))
        val connected = CompletableDeferred<String>()
        val unpaired = Collections.synchronizedList(mutableListOf<String>())
        val loop = ReconnectLoop(store, null,
            sessionFactory = { p -> handle { if (p.deviceId == "other") { connected.complete("other"); awaitCancellation() } else throw IOException("away") } },
            listener = object : ReconnectLoop.Listener { override fun onUnpaired(pc: PairedDevice, remaining: Int) { unpaired += pc.deviceId } },
            backoffMs = listOf(60_000), chooser = PcChooser(InMemoryPcPreferences(pinnedPcId = "old")),
            relaySessionFactory = { p -> if (p.deviceId == "old") handle { throw DeviceUnpairedException("forgotten") } else null })
        val job = launch { loop.run() }
        assertEquals("other", withTimeout(3_000) { connected.await() })
        assertEquals(listOf("old"), unpaired.toList())
        assertEquals(listOf("other"), store.devices.map { it.deviceId })
        job.cancelAndJoin()
    }

    // ------------------------------------------------------------------ over a real session

    private object NoClipboard : ClipboardAccess {
        override fun readText(): String? = null
        override fun writeText(text: String) {}
        override fun setChangeListener(listener: (String) -> Unit) {}
    }

    @Test
    fun the_session_offers_remote_and_keeps_what_the_pc_says() = runBlocking {
        val store = InMemoryRelayConfigStore()
        val features = CompletableDeferred<List<String>>()
        val stored = CompletableDeferred<Unit>()
        TestPc {
            handshake()
            features.complete(receive().payloadAs<CapabilitiesPayload>().features)
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES))))
            send(BridgeMessage.create(MessageType.RELAY_CONFIG, RelayConfigPayload(true, "relay.example.com", 47830, RelayConfig.encodeKey(key))))
            runBlocking { withTimeout(5_000) { stored.await() } }
            send(BridgeMessage.create(MessageType.RELAY_CONFIG, RelayConfigPayload(false)))
            ignoreRest()
        }.use { p ->
            val pcDevice = PairedDevice(p.identity.deviceId, "PC", p.publicKeyBase64(), "127.0.0.1", p.port)
            val s = BridgeClientSession(SoftwareIdentity(), pcDevice, FileTransferClient {}, ClipboardSyncClient(NoClipboard, this),
                Files.createTempDirectory("dl").toFile(), relay = RelayConfigHandler(store))
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            assertTrue(Features.REMOTE in withTimeout(10_000) { features.await() })
            withTimeout(5_000) { while (store.get(pcDevice.deviceId) == null) delay(10) }
            assertArrayEquals(key, store.get(pcDevice.deviceId)!!.key)
            stored.complete(Unit)
            withTimeout(5_000) { while (store.get(pcDevice.deviceId) != null) delay(10) }
            s.close(); job.cancelAndJoin()
        }
    }

    @Test
    fun without_a_relay_handler_the_phone_does_not_offer_remote() = runBlocking {
        val features = CompletableDeferred<List<String>>()
        TestPc {
            handshake(); features.complete(receive().payloadAs<CapabilitiesPayload>().features)
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES))))
            ignoreRest()
        }.use { p ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(p.identity.deviceId, "PC", p.publicKeyBase64(), "127.0.0.1", p.port),
                FileTransferClient {}, ClipboardSyncClient(NoClipboard, this), Files.createTempDirectory("dl").toFile())
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            assertFalse(Features.REMOTE in withTimeout(10_000) { features.await() })
            s.close(); job.cancelAndJoin()
        }
    }
}
