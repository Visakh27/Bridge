package com.bridge.app

import com.bridge.app.network.*
import com.bridge.app.protocol.*
import com.bridge.app.security.FileHasher
import java.io.File
import java.nio.file.Files
import java.util.Base64
import kotlinx.coroutines.*
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phase B on the phone: resume and folders, against TestPc (written from protocol.md). */
class ResumeTest {
    private object NoClipboard : ClipboardAccess {
        override fun readText(): String? = null
        override fun writeText(text: String) {}
        override fun setChangeListener(listener: (String) -> Unit) {}
    }

    private val chunk = FileTransferClient.CHUNK_SIZE
    private fun tmp() = Files.createTempDirectory("bridge").toFile()
    private fun device(pc: TestPc) = PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port)
    private fun bytes(n: Int, seed: Int = 1) = ByteArray(n).also { java.util.Random(seed.toLong()).nextBytes(it) }

    /** Connects a phone session to [pc] and returns it once capabilities are exchanged. */
    private suspend fun CoroutineScope.connect(pc: TestPc, files: FileTransferClient, dl: File, phone: SoftwareIdentity): Pair<BridgeClientSession, Job> {
        val up = CompletableDeferred<Unit>()
        val s = BridgeClientSession(phone, device(pc), files, ClipboardSyncClient(NoClipboard, this), dl,
            listener = object : BridgeClientSession.Listener { override fun onConnected(peerFeatures: List<String>) { up.complete(Unit) } })
        val job = launch(Dispatchers.IO) { runCatching { s.run() } }
        withTimeout(5_000) { up.await() }
        return s to job
    }

    private fun TestPc.Connection.hello(features: List<String> = listOf(Features.FILES, Features.FILES_RESUME, Features.FILES_FOLDERS)) {
        handshake()
        val caps = receive().payloadAs<CapabilitiesPayload>()
        check(Features.FILES_RESUME in caps.features && Features.FILES_FOLDERS in caps.features) { "phone must advertise resume+folders" }
        send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(features)))
    }

    @Test
    fun phone_sender_continues_from_the_index_the_pc_asks_for() = runBlocking {
        val data = bytes(chunk * 10 + 77)
        val firstIndex = CompletableDeferred<Int>()
        val gotEnd = CompletableDeferred<Unit>()
        TestPc {
            hello()
            val start = receive().payloadAs<FileStartPayload>()
            check(start.resumable == true)
            send(BridgeMessage.create(MessageType.FILE_RESUME, FileResumePayload(start.transferId, 6))) // "I already have 6 chunks"
            while (true) {
                val m = receive()
                if (m.type == MessageType.FILE_CHUNK && !firstIndex.isCompleted) firstIndex.complete(m.payloadAs<FileChunkPayload>().index)
                if (m.type == MessageType.FILE_END) { send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(start.transferId, "ok"))); gotEnd.complete(Unit); break }
            }
            ignoreRest()
        }.use { pc ->
            val dir = tmp()
            val src = File(dir, "a.bin").apply { writeBytes(data) }
            val files = FileTransferClient(TransferStateStore(File(dir, "state"))) {}
            val (s, job) = connect(pc, files, dir, SoftwareIdentity())
            val result = s.sendFile(src)!!
            assertEquals(6, withTimeout(5_000) { firstIndex.await() })
            assertEquals(TransferStatus.COMPLETED, result.status)
            assertEquals(6, result.resumedFrom)
            assertTrue(files.state.pendingFor(pc.identity.deviceId).isEmpty())
            s.close(); job.cancelAndJoin()
        }
    }

    @Test
    fun phone_sender_reports_interrupted_and_keeps_queue_when_pc_vanishes_before_ack() = runBlocking {
        TestPc {
            hello()
            val start = receive().payloadAs<FileStartPayload>()
            send(BridgeMessage.create(MessageType.FILE_RESUME, FileResumePayload(start.transferId, 0)))
            while (receive().type != MessageType.FILE_END) { }
            close() // PC "crashes" before acking
        }.use { pc ->
            val dir = tmp()
            val src = File(dir, "b.bin").apply { writeBytes(bytes(chunk * 3)) }
            val files = FileTransferClient(TransferStateStore(File(dir, "state"))) {}
            val (s, job) = connect(pc, files, dir, SoftwareIdentity())
            val result = withTimeout(10_000) { s.sendFile(src)!! }
            assertEquals(TransferStatus.INTERRUPTED, result.status) // never confirmed: not "sent"
            assertEquals(1, files.state.pendingFor(pc.identity.deviceId).size)
            job.cancelAndJoin()
        }
    }

    @Test
    fun ack_ok_in_answer_to_file_start_means_already_done() = runBlocking {
        val chunks = java.util.concurrent.atomic.AtomicInteger()
        TestPc {
            hello()
            val start = receive().payloadAs<FileStartPayload>()
            send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(start.transferId, "ok"))) // "I stored it last time"
            try { while (true) { if (receive().type == MessageType.FILE_CHUNK) chunks.incrementAndGet() } } catch (e: Exception) {}
        }.use { pc ->
            val dir = tmp()
            val files = FileTransferClient(TransferStateStore(File(dir, "state"))) {}
            val (s, job) = connect(pc, files, dir, SoftwareIdentity())
            val result = s.sendFile(File(dir, "c.bin").apply { writeBytes(bytes(chunk * 4)) })!!
            delay(300)
            assertEquals(TransferStatus.COMPLETED, result.status)
            assertEquals(0, chunks.get())
            s.close(); job.cancelAndJoin()
        }
    }

    @Test
    fun out_of_range_resume_index_fails_cleanly() = runBlocking {
        TestPc {
            hello()
            val start = receive().payloadAs<FileStartPayload>()
            send(BridgeMessage.create(MessageType.FILE_RESUME, FileResumePayload(start.transferId, 999)))
            ignoreRest()
        }.use { pc ->
            val dir = tmp()
            val files = FileTransferClient(TransferStateStore(File(dir, "state"))) {}
            val (s, job) = connect(pc, files, dir, SoftwareIdentity())
            val result = s.sendFile(File(dir, "d.bin").apply { writeBytes(bytes(chunk * 2)) })!!
            assertEquals(TransferStatus.FAILED, result.status)
            assertTrue(files.state.pendingFor(pc.identity.deviceId).isEmpty())
            s.close(); job.cancelAndJoin()
        }
    }

    /** A PC that offers [data] resumably, sending chunks from wherever the phone says, optionally dropping after [dropAfter] chunks. */
    private fun pcSender(id: String, name: String, data: ByteArray, dropAfter: Int?, onResume: (Int) -> Unit, ack: CompletableDeferred<String>?, relativePath: String? = null) =
        TestPc {
            hello()
            val total = ((data.size + chunk - 1) / chunk)
            send(BridgeMessage.create(MessageType.FILE_START, FileStartPayload(id, name, data.size.toLong(), FileHasher.sha256Hex(data), total,
                resumable = true, relativePath = relativePath, batchId = relativePath?.let { "batch-1" })))
            var next = -1
            while (next < 0) {
                val m = receive()
                if (m.type == MessageType.FILE_RESUME) next = m.payloadAs<FileResumePayload>().nextIndex
                if (m.type == MessageType.FILE_ACK) { ack?.complete(m.payloadAs<FileAckPayload>().status); ignoreRest(); return@TestPc }
            }
            onResume(next)
            for (i in next until total) {
                if (dropAfter != null && i - next >= dropAfter) { Thread.sleep(200); close(); return@TestPc }
                val slice = data.copyOfRange(i * chunk, minOf(data.size, (i + 1) * chunk))
                send(BridgeMessage.create(MessageType.FILE_CHUNK, FileChunkPayload(id, i, Base64.getEncoder().encodeToString(slice))))
            }
            send(BridgeMessage.create(MessageType.FILE_END, FileEndPayload(id)))
            while (true) { val m = receive(); if (m.type == MessageType.FILE_ACK) { ack?.complete(m.payloadAs<FileAckPayload>().status); break } }
            ignoreRest()
        }

    @Test
    fun phone_receiver_keeps_partial_across_reconnect_resumes_and_answers_reoffer_with_ok() = runBlocking {
        val dl = tmp()
        val state = File(tmp(), "state")
        val phone = SoftwareIdentity()
        val data = bytes(chunk * 12 + 5)
        val received = mutableListOf<TransferProgress>()
        fun client() = FileTransferClient(TransferStateStore(state)) { synchronized(received) { received += it } }

        // 1st connection: the PC drops after 5 chunks.
        val firstResume = CompletableDeferred<Int>()
        pcSender("t1", "movie.mkv", data, dropAfter = 5, onResume = { firstResume.complete(it) }, ack = null).use { pc ->
            val (_, job) = connect(pc, client(), dl, phone)
            assertEquals(0, withTimeout(5_000) { firstResume.await() })
            withTimeout(5_000) { job.join() }
        }
        assertTrue(synchronized(received) { received.any { it.status == TransferStatus.INTERRUPTED } })
        assertEquals(1, File(dl, FileTransferClient.PARTIAL_DIR_NAME).listFiles()!!.count { it.name.endsWith(".part") })
        assertTrue(dl.listFiles()!!.none { it.isFile }) // nothing half-done in the download folder itself

        // 2nd connection (app restarted too: new client, same state dir): continues at chunk 5.
        val secondResume = CompletableDeferred<Int>()
        val ack2 = CompletableDeferred<String>()
        pcSender("t1", "movie.mkv", data, dropAfter = null, onResume = { secondResume.complete(it) }, ack = ack2).use { pc ->
            val (s, job) = connect(pc, client(), dl, phone)
            assertEquals(5, withTimeout(5_000) { secondResume.await() })
            assertEquals("ok", withTimeout(5_000) { ack2.await() })
            s.close(); job.cancelAndJoin()
        }
        assertArrayEquals(data, File(dl, "movie.mkv").readBytes())

        // 3rd: the PC missed that ack and offers again → "ok" at once, no duplicate file.
        val ack3 = CompletableDeferred<String>()
        pcSender("t1", "movie.mkv", data, dropAfter = null, onResume = { error("must not resume a finished file") }, ack = ack3).use { pc ->
            val (s, job) = connect(pc, client(), dl, phone)
            assertEquals("ok", withTimeout(5_000) { ack3.await() })
            s.close(); job.cancelAndJoin()
        }
        assertEquals(listOf("movie.mkv"), dl.listFiles()!!.filter { it.isFile }.map { it.name })
    }

    @Test
    fun phone_receiver_puts_folder_files_in_their_folders() = runBlocking {
        val dl = tmp()
        val ack = CompletableDeferred<String>()
        pcSender("f1", "b.raw", "bbb".toByteArray(), null, {}, ack, relativePath = "Holiday/day 1/raw").use { pc ->
            val (s, job) = connect(pc, FileTransferClient {}, dl, SoftwareIdentity())
            assertEquals("ok", withTimeout(5_000) { ack.await() })
            s.close(); job.cancelAndJoin()
        }
        assertEquals("bbb", File(dl, "Holiday/day 1/raw/b.raw").readText())
    }

    @Test
    fun phone_receiver_keeps_hostile_folder_paths_inside_downloads() = runBlocking {
        val dl = File(tmp(), "Bridge").apply { mkdirs() }
        for ((i, rel) in listOf("../../escape", "/data/data/com.bridge.app/shared_prefs", "a/../../b").withIndex()) {
            val ack = CompletableDeferred<String>()
            pcSender("h$i", "x.txt", "evil".toByteArray(), null, {}, ack, relativePath = rel).use { pc ->
                val (s, job) = connect(pc, FileTransferClient {}, dl, SoftwareIdentity())
                withTimeout(5_000) { ack.await() }
                s.close(); job.cancelAndJoin()
            }
        }
        val outside = dl.parentFile!!.walkTopDown().filter { it.name == "x.txt" && !it.canonicalPath.startsWith(dl.canonicalPath + File.separator) }.toList()
        assertTrue("escaped: $outside", outside.isEmpty())
        assertTrue(File(dl, "escape/x.txt").exists())
    }

    // ---- pure helpers ----

    @Test
    fun state_store_persists_expires_and_deletes_private_copies() {
        val dir = tmp()
        var now = 1_000_000L
        val copy = File(dir, "copy.bin").apply { writeText("x") }
        TransferStateStore(dir) { now }.use { it.addOutbox(TransferStateStore.OutboxEntry("t", "pc", copy.path, "copy.bin", created = now, deleteWhenDone = true)) }
        assertEquals(1, TransferStateStore(dir) { now }.pendingFor("pc").size) // survives a restart
        now += TransferStateStore.LIFETIME_MS + 1
        assertTrue(TransferStateStore(dir) { now }.pendingFor("pc").isEmpty()) // expired…
        assertFalse(copy.exists()) // …and its private copy cleaned up
    }

    @Test
    fun folder_items_keep_structure_skip_symlinks_and_cap_depth() {
        val root = File(tmp(), "Album").apply { mkdirs() }
        File(root, "a.jpg").writeText("a")
        File(root, "sub/deeper").mkdirs(); File(root, "sub/deeper/b.jpg").writeText("b")
        Files.createSymbolicLink(File(root, "loop").toPath(), root.toPath()) // would recurse forever
        val items = FileTransferClient.folderItems(root)
        assertEquals(listOf("Album" to "a.jpg", "Album/sub/deeper" to "b.jpg"), items.map { it.relativePath to it.name })
    }

    @Test
    fun safe_relative_path_rules() {
        assertEquals(listOf("Holiday", "day 1"), SafeRelativePath.segments("Holiday/./day 1/"))
        assertEquals(listOf("a", "b"), SafeRelativePath.segments("a/../b"))
        assertEquals(null, SafeRelativePath.segments((1..20).joinToString("/") { "d" }))
        val dl = tmp()
        assertNotNull(SafeRelativePath.resolve(dl, "Holiday", "Holiday/x"))
        File(dl, "Holiday").mkdirs()
        assertEquals("Holiday (1)", SafeRelativePath.uniqueRootName(dl, "Holiday"))
    }

    @Test
    fun expired_partials_are_removed() {
        val dl = tmp()
        val dir = File(dl, FileTransferClient.PARTIAL_DIR_NAME).apply { mkdirs() }
        val old = File(dir, "a.part").apply { writeText("x") }
        val oldMeta = File(dir, "a.json").apply { writeText("{}"); setLastModified(System.currentTimeMillis() - TransferStateStore.LIFETIME_MS - 60_000) }
        val fresh = File(dir, "b.part").apply { writeText("y") }
        FileTransferClient.cleanupExpiredPartials(dl)
        assertFalse(old.exists()); assertFalse(oldMeta.exists()); assertTrue(fresh.exists())
    }

    @Test
    fun folder_to_pc_without_resume_cleans_every_private_copy_when_cut_short() = runBlocking {
        TestPc {
            hello(features = listOf(Features.FILES, Features.FILES_FOLDERS)) // no resume support
            val first = receive().payloadAs<FileStartPayload>()
            check(first.resumable == null && first.batchCount == 5)
            while (receive().type != MessageType.FILE_END) { }
            send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(first.transferId, "ok")))
            receive() // start of file 2…
            close()   // …and the PC disappears
        }.use { pc ->
            val dir = tmp()
            val copies = (0 until 5).map { File(dir, "copy$it").apply { writeBytes(bytes(chunk * 2, it)) } }
            val items = copies.mapIndexed { i, f -> FolderItem(f, "Pics", deleteWhenDone = true, name = "p$i.jpg") }
            val (s, job) = connect(pc, FileTransferClient {}, dir, SoftwareIdentity())
            val results = withTimeout(10_000) { s.sendFolder(items)!! }
            assertEquals(TransferStatus.COMPLETED, results[0].status)
            assertTrue("copies left: ${copies.filter { it.exists() }.map { it.name }}", copies.none { it.exists() })
            job.cancelAndJoin()
        }
    }
}
