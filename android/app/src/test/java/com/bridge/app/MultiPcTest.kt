package com.bridge.app

import com.bridge.app.network.*
import java.io.IOException
import java.util.Collections
import kotlinx.coroutines.*
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phase H on the phone: several paired PCs, one connection at a time. */
class MultiPcTest {
    private fun pc(id: String, name: String = id) = PairedDevice(id, name, "AAAA", "127.0.0.1", 1)

    private class MemStore(vararg initial: PairedDevice) : PairedDeviceStore {
        val devices: MutableList<PairedDevice> = Collections.synchronizedList(initial.toMutableList())
        override suspend fun save(device: PairedDevice) { devices.removeAll { it.deviceId == device.deviceId }; devices.add(device) }
        override suspend fun list() = devices.toList()
        override suspend fun revoke(deviceId: String) { devices.removeAll { it.deviceId == deviceId } }
    }

    /** A session per attempt; [behaviour] decides how the attempt at each PC goes. */
    private class Scripted(val behaviour: suspend (PairedDevice) -> Unit) {
        val attempts: MutableList<String> = Collections.synchronizedList(mutableListOf())
        @Volatile var live: CompletableDeferred<Unit>? = null
        fun factory(): (PairedDevice) -> SessionHandle = { pc ->
            object : SessionHandle {
                val done = CompletableDeferred<Unit>()
                override suspend fun run() {
                    attempts += pc.deviceId
                    live = done
                    coroutineScope {
                        val b = async { behaviour(pc) }
                        select2(b, done)
                    }
                }
                override fun close() { done.complete(Unit) }
            }
        }

        /** Runs the behaviour until it ends or the session is closed. */
        private suspend fun select2(b: Deferred<Unit>, closed: CompletableDeferred<Unit>) {
            kotlinx.coroutines.selects.select<Unit> {
                b.onAwait { }
                closed.onAwait { b.cancel() }
            }
            b.getCompletionExceptionOrNull()?.takeIf { it !is CancellationException }?.let { throw it }
        }
    }

    // ------------------------------------------------------------------ choosing

    @Test
    fun the_chosen_pc_comes_first_then_the_last_one_used_then_newest_pairing() {
        val prefs = InMemoryPcPreferences()
        val chooser = PcChooser(prefs)
        val home = pc("home"); val work = pc("work"); val laptop = pc("laptop")
        val paired = listOf(home, work, laptop) // oldest pairing first
        assertEquals(listOf("laptop", "work", "home"), chooser.order(paired).map { it.deviceId })
        chooser.connected("home")
        assertEquals(listOf("home", "laptop", "work"), chooser.order(paired).map { it.deviceId })
        chooser.pin("work")
        assertEquals(listOf("work", "home", "laptop"), chooser.order(paired).map { it.deviceId })
        chooser.connected("work") // pinned and last are the same PC: listed once
        assertEquals(listOf("work", "laptop", "home"), chooser.order(paired).map { it.deviceId })
        chooser.forgotten("work")
        assertNull(prefs.pinnedPcId)
        assertNull(prefs.lastConnectedPcId)
        assertEquals(listOf("laptop", "home"), chooser.order(listOf(home, laptop)).map { it.deviceId })
        chooser.pin("gone") // a choice that's no longer paired is skipped
        assertEquals("laptop", chooser.preferred(listOf(home, laptop))?.deviceId)
        assertEquals(emptyList<PairedDevice>(), chooser.order(emptyList()))
    }

    // ------------------------------------------------------------------ the reconnect loop

    @Test
    fun an_unreachable_pc_falls_through_to_the_next_without_waiting() = runBlocking {
        val chooser = PcChooser(InMemoryPcPreferences(lastConnectedPcId = "home"))
        val connected = CompletableDeferred<String>()
        val script = Scripted { p -> if (p.deviceId == "home") throw IOException("not here") else { connected.complete(p.deviceId); awaitCancellation() } }
        val loop = ReconnectLoop(MemStore(pc("home"), pc("work")), null, script.factory(), backoffMs = listOf(60_000), chooser = chooser)
        val job = launch { loop.run() }
        assertEquals("work", withTimeout(3_000) { connected.await() }) // a backoff would blow the timeout
        assertEquals(listOf("home", "work"), script.attempts.toList())
        assertEquals("work", loop.currentPc?.deviceId)
        job.cancelAndJoin()
    }

    @Test
    fun when_every_pc_fails_it_backs_off_once_per_round() = runBlocking {
        val script = Scripted { throw IOException("nothing reachable") }
        val waits = Collections.synchronizedList(mutableListOf<Long>())
        val loop = ReconnectLoop(MemStore(pc("a"), pc("b"), pc("c")), null, script.factory(),
            listener = object : ReconnectLoop.Listener { override fun onRetryScheduled(delayMs: Long) { waits += delayMs } },
            backoffMs = listOf(150, 60_000), chooser = PcChooser(InMemoryPcPreferences()))
        val job = launch { loop.run() }
        withTimeout(3_000) { while (script.attempts.size < 6) delay(10) }
        delay(300)
        job.cancelAndJoin()
        assertEquals(listOf("c", "b", "a", "c", "b", "a"), script.attempts.toList())
        assertEquals(listOf(150L, 60_000L), waits.toList())
    }

    @Test
    fun a_pc_that_forgot_this_phone_is_dropped_and_the_others_still_work() = runBlocking {
        val store = MemStore(pc("old"), pc("work"))
        val chooser = PcChooser(InMemoryPcPreferences(pinnedPcId = "old"))
        val unpaired = CompletableDeferred<Pair<String, Int>>()
        val connected = CompletableDeferred<String>()
        val script = Scripted { p -> if (p.deviceId == "old") throw DeviceUnpairedException("forgotten") else { connected.complete(p.deviceId); awaitCancellation() } }
        val loop = ReconnectLoop(store, null, script.factory(),
            listener = object : ReconnectLoop.Listener { override fun onUnpaired(pc: PairedDevice, remaining: Int) { unpaired.complete(pc.deviceId to remaining) } },
            backoffMs = listOf(60_000), chooser = chooser)
        val job = launch { loop.run() }
        assertEquals("old" to 1, withTimeout(3_000) { unpaired.await() })
        assertEquals("work", withTimeout(3_000) { connected.await() })
        assertEquals(listOf("work"), store.devices.map { it.deviceId })
        assertNull(chooser.pinnedPcId)
        job.cancelAndJoin()
    }

    @Test
    fun the_loop_ends_unpaired_only_when_every_pc_forgot_this_phone() = runBlocking {
        val store = MemStore(pc("a"), pc("b"))
        val loop = ReconnectLoop(store, null, Scripted { throw DeviceUnpairedException("forgotten") }.factory(), chooser = PcChooser(InMemoryPcPreferences()))
        assertEquals(ReconnectLoop.Outcome.UNPAIRED, withTimeout(3_000) { loop.run() })
        assertTrue(store.devices.isEmpty())
    }

    @Test
    fun choosing_another_pc_leaves_the_current_one_at_once() = runBlocking {
        val prefs = InMemoryPcPreferences(lastConnectedPcId = "home")
        val chooser = PcChooser(prefs)
        val script = Scripted { p -> chooser.connected(p.deviceId); awaitCancellation() } // every PC answers
        val loop = ReconnectLoop(MemStore(pc("home"), pc("work")), null, script.factory(), backoffMs = listOf(60_000), chooser = chooser)
        val job = launch { loop.run() }
        withTimeout(3_000) { while (loop.currentPc?.deviceId != "home") delay(10) }
        chooser.pin("work")
        loop.reconsider()
        withTimeout(3_000) { while (loop.currentPc?.deviceId != "work") delay(10) } // no 60 s wait
        assertEquals(listOf("home", "work"), script.attempts.toList())
        assertEquals("work", prefs.lastConnectedPcId)
        job.cancelAndJoin()
    }

    @Test
    fun a_full_pc_is_skipped_without_looking_for_it_elsewhere() = runBlocking {
        var discoveries = 0
        val discovery = object : PcDiscovery({ emptyList() }) {
            override suspend fun find(pc: PairedDevice, timeoutMs: Long): DiscoveredPc? { discoveries++; return null }
        }
        val connected = CompletableDeferred<String>()
        val script = Scripted { p -> if (p.deviceId == "busy") throw PcFullException("8 phones") else { connected.complete(p.deviceId); awaitCancellation() } }
        val loop = ReconnectLoop(MemStore(pc("other"), pc("busy")), discovery, script.factory(), backoffMs = listOf(60_000), chooser = PcChooser(InMemoryPcPreferences()))
        val job = launch { loop.run() }
        assertEquals("other", withTimeout(3_000) { connected.await() })
        assertEquals(0, discoveries) // it answered, so it hasn't moved
        job.cancelAndJoin()
    }

    @Test
    fun without_a_chooser_it_keeps_the_old_single_pc_behaviour() = runBlocking {
        val script = Scripted { awaitCancellation() }
        val loop = ReconnectLoop(MemStore(pc("first"), pc("newest")), null, script.factory(), backoffMs = listOf(60_000))
        val job = launch { loop.run() }
        withTimeout(3_000) { while (script.attempts.isEmpty()) delay(10) }
        delay(200)
        assertEquals(listOf("newest"), script.attempts.toList())
        job.cancelAndJoin()
    }

    @Test
    fun the_pc_full_error_from_a_real_pc_is_its_own_failure() = runBlocking {
        val failed = CompletableDeferred<Throwable>()
        TestPc {
            handshake()
            receive() // capabilities
            send(com.bridge.app.protocol.BridgeMessage.create(com.bridge.app.protocol.MessageType.ERROR,
                com.bridge.app.protocol.ErrorPayload(com.bridge.app.protocol.ErrorCodes.TOO_MANY_DEVICES, "This PC already has 8 phones connected")))
            ignoreRest()
        }.use { p ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(p.identity.deviceId, "PC", p.publicKeyBase64(), "127.0.0.1", p.port),
                FileTransferClient {}, ClipboardSyncClient(object : ClipboardAccess {
                    override fun readText(): String? = null
                    override fun writeText(text: String) {}
                    override fun setChangeListener(listener: (String) -> Unit) {}
                }, this), java.nio.file.Files.createTempDirectory("dl").toFile())
            val e = runCatching { withTimeout(10_000) { s.run() } }.exceptionOrNull()
            failed.complete(e ?: AssertionError("no error"))
            s.close()
        }
        val e = failed.await()
        assertTrue("$e", e is PcFullException)
        assertFalse(e is AuthenticationException)
    }
}
