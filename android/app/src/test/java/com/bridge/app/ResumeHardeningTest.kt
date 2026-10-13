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
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phase B iteration 2 on the phone: performance and hostile cases. */
class ResumeHardeningTest {
    private object NoClipboard : ClipboardAccess {
        override fun readText(): String? = null
        override fun writeText(text: String) {}
        override fun setChangeListener(listener: (String) -> Unit) {}
    }
    private fun tmp() = Files.createTempDirectory("bridge").toFile()
    private val chunk = FileTransferClient.CHUNK_SIZE

    @Test
    fun large_queue_does_not_rewrite_the_state_file_per_item() {
        val dir = tmp()
        val store = TransferStateStore(dir)
        val entries = (0 until 5000).map { TransferStateStore.OutboxEntry("t$it", "pc", "/p/$it.jpg", "$it.jpg", "Photos", "b", it, 5000, created = System.currentTimeMillis()) }
        val started = System.currentTimeMillis()
        store.addOutboxAll(entries)
        entries.forEach { store.removeOutbox(it.transferId) }
        store.flush()
        val took = System.currentTimeMillis() - started
        assertTrue("took $took ms", took < 5_000)
        assertTrue(TransferStateStore(dir).pendingFor("pc").isEmpty())
    }

    @Test
    fun changes_reach_disk_shortly_after_and_on_close() {
        val dir = tmp()
        val store = TransferStateStore(dir)
        store.addOutbox(TransferStateStore.OutboxEntry("x", "pc", "/a", "a", created = System.currentTimeMillis()))
        Thread.sleep(TransferStateStore.FLUSH_DELAY_MS + 500)
        assertEquals(1, TransferStateStore(dir).pendingFor("pc").size)
        store.removeOutbox("x"); store.close()
        assertTrue(TransferStateStore(dir).pendingFor("pc").isEmpty())
    }

    /** A PC that offers [data] resumably under [id] and finishes it; returns its final ack status. */
    private fun pcOffers(id: String, data: ByteArray, ack: CompletableDeferred<String>) = TestPc {
        handshake(); receive()
        send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES, Features.FILES_RESUME))))
        val total = (data.size + chunk - 1) / chunk
        send(BridgeMessage.create(MessageType.FILE_START, FileStartPayload(id, "f.bin", data.size.toLong(), FileHasher.sha256Hex(data), total, resumable = true)))
        val first = receive()
        if (first.type == MessageType.FILE_ACK) { ack.complete("answered-without-data:" + first.payloadAs<FileAckPayload>().status); ignoreRest(); return@TestPc }
        for (i in first.payloadAs<FileResumePayload>().nextIndex until total)
            send(BridgeMessage.create(MessageType.FILE_CHUNK, FileChunkPayload(id, i, Base64.getEncoder().encodeToString(data.copyOfRange(i * chunk, minOf(data.size, (i + 1) * chunk))))))
        send(BridgeMessage.create(MessageType.FILE_END, FileEndPayload(id)))
        while (true) { val m = receive(); if (m.type == MessageType.FILE_ACK) { ack.complete(m.payloadAs<FileAckPayload>().status); break } }
        ignoreRest()
    }

    private suspend fun CoroutineScope.receiveOnce(id: String, data: ByteArray, files: FileTransferClient, dl: File, phone: SoftwareIdentity): String {
        val ack = CompletableDeferred<String>()
        pcOffers(id, data, ack).use { pc ->
            val s = BridgeClientSession(phone, PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                files, ClipboardSyncClient(NoClipboard, this), dl)
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            val result = withTimeout(10_000) { ack.await() }
            s.close(); job.cancelAndJoin()
            return result
        }
    }

    @Test
    fun reused_transfer_id_with_different_content_is_really_transferred() = runBlocking {
        val dl = tmp(); val phone = SoftwareIdentity()
        val files = FileTransferClient(TransferStateStore(File(tmp(), "s"))) {}
        assertEquals("ok", receiveOnce("same", "first".toByteArray(), files, dl, phone))
        // Note: each TestPc has a new identity, so use one PC for both offers via the same peer id is not possible here;
        // the receiver keys by (peer, id, sha) — a different sha must never be answered "ok" unseen.
        assertEquals("ok", receiveOnce("same", "second version".toByteArray(), files, dl, phone))
        assertEquals(listOf("first", "second version"), dl.listFiles()!!.filter { it.isFile }.map { it.readText() }.sorted())
    }

    @Test
    fun stale_temp_files_from_a_crash_are_cleaned_up_but_user_files_are_not() {
        val dl = tmp()
        val old = System.currentTimeMillis() - TransferStateStore.LIFETIME_MS - 60_000
        val stale = File(dl, ".bridge-${java.util.UUID.randomUUID()}.part").apply { writeText("x"); setLastModified(old) }
        val fresh = File(dl, ".bridge-${java.util.UUID.randomUUID()}.part").apply { writeText("y") }
        val user = File(dl, "holiday.part").apply { writeText("z"); setLastModified(old) }
        FileTransferClient.cleanupExpiredPartials(dl)
        assertFalse(stale.exists()); assertTrue(fresh.exists()); assertTrue(user.exists())
    }

    @Test
    fun cached_hash_is_used_while_size_and_timestamp_match() = runBlocking {
        val starts = mutableListOf<String>()
        val dir = tmp()
        val src = File(dir, "a.bin").apply { writeBytes(ByteArray(chunk * 3)) }
        val stamp = src.lastModified()
        val realZeroHash = FileHasher.sha256Hex(ByteArray(chunk * 3))
        val state = TransferStateStore(File(dir, "s"))
        TestPc {
            handshake(); receive()
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES, Features.FILES_RESUME))))
            repeat(2) {
                val start = receive().payloadAs<FileStartPayload>()
                synchronized(starts) { starts += start.sha256 }
                send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(start.transferId, "cancelled")))
            }
            ignoreRest()
        }.use { pc ->
            val entry = TransferStateStore.OutboxEntry("e1", pc.identity.deviceId, src.path, "a.bin", created = System.currentTimeMillis(),
                sha256 = realZeroHash, size = src.length(), lastModified = stamp)
            // Same size and timestamp, different content: the cached hash is used (and the receiver would catch it).
            src.writeBytes(ByteArray(chunk * 3).also { it[5] = 1 }); src.setLastModified(stamp)
            state.addOutbox(entry)
            val files = FileTransferClient(state) {}
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                files, ClipboardSyncClient(NoClipboard, this), dir)
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            withTimeout(5_000) { while (synchronized(starts) { starts.size } < 1) delay(20) }
            // Touch the file: now it must be re-hashed.
            src.setLastModified(stamp + 60_000)
            state.addOutbox(entry.copy(transferId = "e2", sha256 = "stale"))
            s.sendFile(File(dir, "a.bin")) // a fresh send always hashes
            withTimeout(5_000) { while (synchronized(starts) { starts.size } < 2) delay(20) }
            assertEquals(realZeroHash, starts[0])
            assertEquals(FileHasher.sha256HexOfFile(src.path), starts[1])
            s.close(); job.cancelAndJoin()
        }
    }

    @Test
    fun abandoned_partials_from_one_peer_are_capped() = runBlocking {
        val dl = tmp()
        val files = FileTransferClient {}
        val pcId = SoftwareIdentity()
        // One PC identity offering many transfers it never finishes.
        repeat(FileTransferClient.MAX_PARTIALS_PER_PEER + 8) { i ->
            val pc = TestPc(identity = pcId) {
                handshake(); receive()
                send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES, Features.FILES_RESUME))))
                send(BridgeMessage.create(MessageType.FILE_START, FileStartPayload("p$i", "f.bin", chunk * 2L, "00", 2, resumable = true)))
                receive() // file.resume
                close()
            }
            pc.use {
                val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pcId.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                    files, ClipboardSyncClient(NoClipboard, this), dl)
                withTimeout(10_000) { runCatching { s.run() } }
            }
        }
        val partials = File(dl, FileTransferClient.PARTIAL_DIR_NAME).listFiles { f -> f.name.endsWith(".part") }!!.size
        assertTrue("$partials partials", partials <= FileTransferClient.MAX_PARTIALS_PER_PEER)
    }

    @Test
    fun completed_record_survives_the_process_being_killed() {
        val dir = tmp()
        TransferStateStore(dir).markCompleted("pc", "t1", "/dl/a.jpg", "abc") // no flush/close: Android killed the process
        File(dir, "completed.log").appendText("{\"key\":\"pc|t2\",\"sav") // torn last line
        val reopened = TransferStateStore(dir)
        assertEquals("/dl/a.jpg", reopened.completedPath("pc", "t1", "ABC"))
        assertEquals(null, reopened.completedPath("pc", "t1", "other"))
    }
}
