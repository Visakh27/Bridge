package com.bridge.app

import com.bridge.app.network.*
import com.bridge.app.protocol.BridgeJson
import java.io.IOException
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.Inet4Address
import java.net.InetAddress
import java.net.InetSocketAddress
import java.util.Base64
import kotlinx.coroutines.*
import org.junit.Assert.assertEquals
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phase A: phone-side discovery and the reconnect loop, against a responder written from the spec. */
class DiscoveryTest {
    /** Minimal PC responder per protocol.md; can also send a forged reply first. */
    private class SpecResponder(val pc: SoftwareIdentity, @Volatile var tcpPort: Int, val forgeFirst: Boolean = false) : AutoCloseable {
        val socket = DatagramSocket(InetSocketAddress(InetAddress.getLoopbackAddress(), 0))
        @Volatile var probes = 0
        private val thread = kotlin.concurrent.thread(isDaemon = true) {
            val buf = ByteArray(1024)
            while (!socket.isClosed) {
                val p = DatagramPacket(buf, buf.size)
                try { socket.receive(p) } catch (e: Exception) { break }
                val probe = runCatching { BridgeJson.decodeFromString(DiscoveryWire.Probe.serializer(), String(p.data, 0, p.length)) }.getOrNull() ?: continue
                val nonce = Base64.getDecoder().decode(probe.nonce)
                if (!Base64.getDecoder().decode(probe.tag).contentEquals(DiscoveryWire.tag(pc.publicKeyBytes(), nonce))) continue
                probes++
                fun reply(signer: SoftwareIdentity, port: Int) {
                    val sig = signer.sign(DiscoveryWire.signedData(nonce, pc.deviceId, port))
                    val bytes = BridgeJson.encodeToString(DiscoveryWire.Reply.serializer(),
                        DiscoveryWire.Reply("here", 1, probe.nonce, pc.deviceId, port, Base64.getEncoder().encodeToString(sig))).toByteArray()
                    socket.send(DatagramPacket(bytes, bytes.size, p.socketAddress))
                }
                if (forgeFirst) reply(SoftwareIdentity(pc.deviceId), 6666) // attacker steering the phone elsewhere
                reply(pc, tcpPort)
            }
        }
        val port: Int get() = socket.localPort
        override fun close() = socket.close()
    }

    private fun paired(pc: SoftwareIdentity, port: Int = 1) =
        PairedDevice(pc.deviceId, "PC", Base64.getEncoder().encodeToString(pc.publicKeyBytes()), "127.0.0.1", port)

    @Test
    fun finds_pc_and_reports_its_signed_port() = runBlocking {
        val pc = SoftwareIdentity()
        SpecResponder(pc, tcpPort = 50123).use { r ->
            val found = PcDiscovery({ listOf(InetAddress.getLoopbackAddress()) }, r.port).find(paired(pc))
            assertEquals(DiscoveredPc("127.0.0.1", 50123), found)
        }
    }

    @Test
    fun ignores_forged_replies() = runBlocking {
        val pc = SoftwareIdentity()
        SpecResponder(pc, tcpPort = 50124, forgeFirst = true).use { r ->
            val found = PcDiscovery({ listOf(InetAddress.getLoopbackAddress()) }, r.port).find(paired(pc))
            assertEquals(50124, found?.port) // the forged 6666 reply arrived first and was rejected
        }
    }

    @Test
    fun returns_null_when_nobody_answers_and_respects_timeout() = runBlocking {
        val pc = SoftwareIdentity()
        SpecResponder(SoftwareIdentity(), tcpPort = 1).use { other -> // a different PC: stays silent
            val started = System.currentTimeMillis()
            assertNull(PcDiscovery({ listOf(InetAddress.getLoopbackAddress()) }, other.port).find(paired(pc), timeoutMs = 800))
            assertTrue(System.currentTimeMillis() - started < 2_000)
            assertEquals(0, other.probes)
        }
    }

    @Test
    fun survives_unreachable_targets() = runBlocking {
        val pc = SoftwareIdentity()
        SpecResponder(pc, tcpPort = 50125).use { r ->
            val targets = { listOf(InetAddress.getByName("203.0.113.1"), InetAddress.getLoopbackAddress()) }
            assertEquals(50125, PcDiscovery(targets, r.port).find(paired(pc))?.port)
        }
    }

    @Test
    fun cancellation_stops_discovery_promptly() = runBlocking {
        val pc = SoftwareIdentity()
        val job = launch(Dispatchers.IO) { PcDiscovery({ listOf(InetAddress.getLoopbackAddress()) }, 9).find(paired(pc), timeoutMs = 30_000) }
        delay(200)
        val started = System.currentTimeMillis()
        job.cancelAndJoin()
        assertTrue(System.currentTimeMillis() - started < 1_000)
    }

    @Test
    fun directed_broadcast_math() {
        fun b(ip: String, prefix: Int) = DiscoveryWire.directedBroadcast(InetAddress.getByName(ip) as Inet4Address, prefix)?.hostAddress
        assertEquals("192.168.1.255", b("192.168.1.23", 24))
        assertEquals("10.0.63.255", b("10.0.12.1", 18))
        assertEquals("172.16.0.3", b("172.16.0.1", 30))
        assertNull(b("10.0.0.1", 31))
        assertNull(b("10.0.0.1", 32))
    }

    // ---- ReconnectLoop ----

    private class MemStore(vararg initial: PairedDevice) : PairedDeviceStore {
        val devices = initial.toMutableList()
        override suspend fun save(device: PairedDevice) { devices.removeAll { it.deviceId == device.deviceId }; devices.add(device) }
        override suspend fun list() = devices.toList()
        override suspend fun revoke(deviceId: String) { devices.removeAll { it.deviceId == deviceId } }
    }

    private class Scripted(val behaviour: suspend (PairedDevice) -> Unit) {
        val attempts = mutableListOf<PairedDevice>()
        fun factory(): (PairedDevice) -> SessionHandle = { pc ->
            object : SessionHandle {
                val closed = CompletableDeferred<Unit>()
                override suspend fun run() { attempts += pc; behaviour(pc) }
                override fun close() { closed.complete(Unit) }
            }
        }
    }

    @Test
    fun loop_rediscovers_moved_pc_saves_it_and_connects_immediately() = runBlocking {
        val pc = SoftwareIdentity()
        SpecResponder(pc, tcpPort = 47999).use { r ->
            val store = MemStore(paired(pc, port = 47820))
            val connected = CompletableDeferred<PairedDevice>()
            val script = Scripted { dev -> if (dev.port == 47820) throw IOException("connection refused") else { connected.complete(dev); awaitCancellation() } }
            val moved = mutableListOf<PairedDevice>()
            val loop = ReconnectLoop(store, PcDiscovery({ listOf(InetAddress.getLoopbackAddress()) }, r.port), script.factory(),
                listener = object : ReconnectLoop.Listener { override fun onAddressChanged(pc: PairedDevice) { moved += pc } },
                backoffMs = listOf(60_000)) // a backoff would blow the timeout: the retry must be immediate
            val job = launch { loop.run() }
            val dev = withTimeout(5_000) { connected.await() }
            assertEquals(47999, dev.port)
            assertEquals(47999, store.devices.single().port) // persisted for next time
            assertEquals(1, moved.size)
            job.cancelAndJoin()
        }
    }

    @Test
    fun loop_stops_and_forgets_pc_when_unpaired() = runBlocking {
        val pc = SoftwareIdentity()
        val store = MemStore(paired(pc))
        val loop = ReconnectLoop(store, null, Scripted { throw DeviceUnpairedException("forgotten") }.factory())
        assertEquals(ReconnectLoop.Outcome.UNPAIRED, withTimeout(5_000) { loop.run() })
        assertTrue(store.devices.isEmpty())
    }

    @Test
    fun loop_returns_when_nothing_is_paired() = runBlocking {
        assertEquals(ReconnectLoop.Outcome.NOT_PAIRED, ReconnectLoop(MemStore(), null, Scripted {}.factory()).run())
    }

    @Test
    fun wake_cuts_the_backoff_short() = runBlocking {
        val pc = SoftwareIdentity()
        val script = Scripted { throw IOException("no network") }
        val loop = ReconnectLoop(MemStore(paired(pc)), null, script.factory(), backoffMs = listOf(60_000))
        val job = launch { loop.run() }
        delay(300)
        assertEquals(1, script.attempts.size)
        loop.wake() // e.g. Wi-Fi came back
        withTimeout(2_000) { while (script.attempts.size < 2) delay(20) }
        job.cancelAndJoin()
    }

    @Test
    fun dropCurrentSession_closes_the_live_session() = runBlocking {
        val pc = SoftwareIdentity()
        val closed = CompletableDeferred<Unit>()
        val loop = ReconnectLoop(MemStore(paired(pc)), null, { _ ->
            object : SessionHandle {
                val done = CompletableDeferred<Unit>()
                override suspend fun run() = done.await()
                override fun close() { done.complete(Unit); closed.complete(Unit) }
            }
        }, backoffMs = listOf(60_000))
        val job = launch { loop.run() }
        delay(200)
        loop.dropCurrentSession() // network lost
        withTimeout(2_000) { closed.await() }
        job.cancelAndJoin()
    }

    @Test
    fun auth_failure_at_old_address_also_triggers_discovery() = runBlocking {
        // Another machine now has the PC's old IP: it can't prove the PC's key.
        val pc = SoftwareIdentity()
        SpecResponder(pc, tcpPort = 48001).use { r ->
            val store = MemStore(paired(pc, port = 47820))
            val connected = CompletableDeferred<Int>()
            val script = Scripted { dev -> if (dev.port == 47820) throw AuthenticationException("not the PC") else { connected.complete(dev.port); awaitCancellation() } }
            val job = launch { ReconnectLoop(store, PcDiscovery({ listOf(InetAddress.getLoopbackAddress()) }, r.port), script.factory(), backoffMs = listOf(60_000)).run() }
            assertEquals(48001, withTimeout(5_000) { connected.await() })
            job.cancelAndJoin()
        }
    }

    // ---- iteration 2: hostile / failure cases ----

    @Test
    fun loop_does_not_spin_when_discovery_keeps_pointing_at_addresses_that_fail() = runBlocking {
        // PC with two network adapters: discovery answers from A, then B, then A…
        // but neither address accepts the connection. Without a guard the loop
        // would hammer both with no backoff.
        val pc = SoftwareIdentity()
        val store = MemStore(paired(pc, port = 1))
        var flip = false
        val alternating = object : PcDiscovery({ emptyList() }) {
            override suspend fun find(pc: PairedDevice, timeoutMs: Long): DiscoveredPc? {
                flip = !flip
                return DiscoveredPc(if (flip) "10.0.0.2" else "10.0.0.3", 47820)
            }
        }
        val script = Scripted { throw IOException("refused") }
        val job = launch { ReconnectLoop(store, alternating, script.factory(), backoffMs = listOf(60_000)).run() }
        delay(1_500)
        job.cancelAndJoin()
        assertTrue("attempts in 1.5 s: ${script.attempts.size}", script.attempts.size <= 2)
    }

    @Test
    fun genuine_reply_still_wins_under_a_flood_of_forged_ones() = runBlocking {
        val pc = SoftwareIdentity()
        val socket = DatagramSocket(InetSocketAddress(InetAddress.getLoopbackAddress(), 0))
        val responder = kotlin.concurrent.thread(isDaemon = true) {
            val buf = ByteArray(1024)
            val p = DatagramPacket(buf, buf.size)
            runCatching { socket.receive(p) }.getOrNull() ?: return@thread
            val probe = BridgeJson.decodeFromString(DiscoveryWire.Probe.serializer(), String(p.data, 0, p.length))
            val nonce = Base64.getDecoder().decode(probe.nonce)
            repeat(300) { i ->
                val junk = if (i % 2 == 0) "garbage $i".toByteArray() else BridgeJson.encodeToString(DiscoveryWire.Reply.serializer(),
                    DiscoveryWire.Reply("here", 1, probe.nonce, pc.deviceId, 6000 + i, Base64.getEncoder().encodeToString(ByteArray(70)))).toByteArray()
                socket.send(DatagramPacket(junk, junk.size, p.socketAddress))
            }
            val sig = pc.sign(DiscoveryWire.signedData(nonce, pc.deviceId, 47820))
            val ok = BridgeJson.encodeToString(DiscoveryWire.Reply.serializer(),
                DiscoveryWire.Reply("here", 1, probe.nonce, pc.deviceId, 47820, Base64.getEncoder().encodeToString(sig))).toByteArray()
            socket.send(DatagramPacket(ok, ok.size, p.socketAddress))
        }
        val found = PcDiscovery({ listOf(InetAddress.getLoopbackAddress()) }, socket.localPort).find(paired(pc), timeoutMs = 3_000)
        assertEquals(47820, found?.port)
        socket.close(); responder.join(1_000)
    }

    @Test
    fun loop_survives_discovery_throwing() = runBlocking {
        val pc = SoftwareIdentity()
        val broken = object : PcDiscovery({ throw IllegalStateException("no network") }) {}
        val script = Scripted { throw IOException("refused") }
        val job = launch { ReconnectLoop(MemStore(paired(pc)), broken, script.factory(), backoffMs = listOf(100)).run() }
        delay(700)
        job.cancelAndJoin()
        assertTrue(script.attempts.size >= 3) // kept retrying
    }
}
