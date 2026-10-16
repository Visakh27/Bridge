package com.bridge.app

import com.bridge.app.network.*
import com.bridge.app.photos.*
import com.bridge.app.protocol.*
import com.bridge.app.security.FileHasher
import java.io.File
import java.nio.file.Files
import java.util.Base64
import java.util.Collections
import java.util.concurrent.CountDownLatch
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.*
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotNull
import org.junit.Assert.assertNull
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phase E on the phone: answering the PC's photo requests. */
class PhotosTest {
    private val jpeg = byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 0xFF.toByte(), 0xE0.toByte(), 1, 2, 3)

    /** A library of [n] photos; every third shares its sort key with the next (ties broken by id). */
    private class FakeLibrary(n: Int, val dir: File = Files.createTempDirectory("lib").toFile()) : PhotoLibrary {
        val photos = (1..n).map { i -> LibraryPhoto(PhotoInfo(i.toString(), "IMG_$i.jpg", 1_000L * i, 4000, 3000, 1000L + i, "image/jpeg"), sortKey = 10_000L * ((i + 1) / 3)) }
        @Volatile var permission = true
        @Volatile var gate: CountDownLatch? = null
        @Volatile var failWith: Exception? = null
        val calls = AtomicInteger()
        var thumbBytes: ByteArray? = byteArrayOf(0xFF.toByte(), 0xD8.toByte(), 9, 9)
        val exported = Collections.synchronizedList(mutableListOf<File>())

        private fun touch() { calls.incrementAndGet(); gate?.await(10, TimeUnit.SECONDS); failWith?.let { throw it } }
        override fun hasPermission() = permission
        override fun page(before: PhotoCursor?, limit: Int): List<LibraryPhoto> {
            touch()
            return photos.sortedWith(compareByDescending<LibraryPhoto> { it.sortKey }.thenByDescending { it.info.id.toLong() })
                .filter { before == null || it.sortKey < before.sortKey || (it.sortKey == before.sortKey && it.info.id.toLong() < before.id) }
                .take(limit)
        }
        override fun thumbnail(id: Long, maxSide: Int): ByteArray? { touch(); return if (photos.any { it.info.id == id.toString() }) thumbBytes else null }
        override fun export(id: Long): ExportedPhoto? {
            touch()
            val p = photos.firstOrNull { it.info.id == id.toString() } ?: return null
            val f = File(dir, "copy-$id").apply { writeText("original ${p.info.name}") }
            exported += f
            return ExportedPhoto(f, p.info.name)
        }
    }

    private class Harness(val lib: FakeLibrary, enabled: Boolean = true, maxQueued: Int = 64) {
        @Volatile var enabled = enabled
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val service = PhotoService(lib, { this.enabled }, scope, maxQueued)
        val replies = Collections.synchronizedList(mutableListOf<BridgeMessage>())
        val sent = Collections.synchronizedList(mutableListOf<Triple<File, String, String>>())
        val order = Collections.synchronizedList(mutableListOf<String>())
        @Volatile var replyFails = false
        @Volatile var sendFails = false

        suspend fun ask(m: BridgeMessage) = service.handleIncoming(m, { r ->
            if (replyFails) throw java.io.IOException("connection gone")
            replies += r; order += "reply:${r.type}"
        }) { f, n, id ->
            order += "send"
            if (sendFails) throw java.io.IOException("connection gone")
            sent += Triple(f, n, id)
        }

        fun waitReplies(n: Int) = runBlocking { withTimeout(10_000) { while (replies.size < n) delay(10) } }
    }

    private fun list(id: String, limit: Int, before: String? = null) = BridgeMessage.create(MessageType.PHOTOS_LIST, PhotosListPayload(id, limit, before))
    private fun thumb(id: String, photo: String) = BridgeMessage.create(MessageType.PHOTOS_THUMB, PhotosThumbPayload(id, photo))
    private fun fetch(id: String, photo: String) = BridgeMessage.create(MessageType.PHOTOS_FETCH, PhotosFetchPayload(id, photo))

    @Test
    fun pages_walk_the_whole_library_once_newest_first() = runBlocking {
        val h = Harness(FakeLibrary(25))
        val seen = mutableListOf<String>()
        var cursor: String? = null
        var pages = 0
        do {
            h.replies.clear()
            h.ask(list("r$pages", 10, cursor))
            h.waitReplies(1)
            val page = h.replies.single().payloadAs<PhotosPagePayload>()
            assertEquals(PhotoStatus.OK, page.status)
            assertEquals("r$pages", page.requestId)
            seen += page.items.map { it.id }
            cursor = page.next
            pages++
        } while (cursor != null)
        assertEquals(3, pages)
        assertEquals(25, seen.size)
        assertEquals(seen.toSet().size, seen.size) // no duplicates across equal sort keys
        assertEquals(h.lib.photos.sortedWith(compareByDescending<LibraryPhoto> { it.sortKey }.thenByDescending { it.info.id.toLong() }).map { it.info.id }, seen)
    }

    @Test
    fun page_size_is_capped_and_bad_requests_are_refused() = runBlocking {
        val h = Harness(FakeLibrary(150))
        h.ask(list("big", 500)); h.waitReplies(1)
        assertEquals(PhotoService.MAX_PAGE, h.replies[0].payloadAs<PhotosPagePayload>().items.size)
        val before = h.lib.calls.get()
        for ((i, bad) in listOf(list("a", 0), list("b", 10, "not-a-cursor"), list("c", 10, "1:2:3"), list("d", 10, "-5:1"),
                thumb("e", "../../etc/passwd"), thumb("f", "12a"), thumb("g", "-1"), thumb("h", "1".repeat(20)),
                fetch("i", "/sdcard/DCIM/x.jpg"), fetch("j", "")).withIndex()) {
            h.ask(bad)
            h.waitReplies(2 + i)
        }
        assertEquals(before, h.lib.calls.get()) // the library was never touched
        assertTrue(h.replies.drop(1).all { it.payload["status"].toString().trim('"') == PhotoStatus.INVALID })
        // No request id (or an absurd one): nothing to answer to.
        h.ask(list("", 10)); h.ask(list("x".repeat(65), 10))
        delay(200)
        assertEquals(11, h.replies.size)
    }

    @Test
    fun switched_off_or_without_permission_answers_without_touching_photos() = runBlocking {
        val h = Harness(FakeLibrary(5), enabled = false)
        h.ask(list("1", 10)); h.ask(thumb("2", "1")); h.ask(fetch("3", "1"))
        h.waitReplies(3)
        assertTrue(h.replies.all { it.payload["status"].toString().trim('"') == PhotoStatus.DISABLED })
        h.enabled = true; h.lib.permission = false
        h.ask(list("4", 10)); h.ask(fetch("5", "1"))
        h.waitReplies(5)
        assertTrue(h.replies.drop(3).all { it.payload["status"].toString().trim('"') == PhotoStatus.NO_PERMISSION })
        assertEquals(0, h.lib.calls.get())
        assertTrue(h.sent.isEmpty())
    }

    @Test
    fun previews_are_jpeg_or_a_clear_refusal() = runBlocking {
        val h = Harness(FakeLibrary(3))
        h.lib.thumbBytes = jpeg
        h.ask(thumb("t1", "2")); h.waitReplies(1)
        val ok = h.replies[0].payloadAs<PhotosThumbnailPayload>()
        assertEquals(PhotoStatus.OK, ok.status); assertEquals("image/jpeg", ok.mime); assertEquals("2", ok.id)
        assertTrue(Base64.getDecoder().decode(ok.data).contentEquals(jpeg))
        h.ask(thumb("t2", "99")); h.waitReplies(2)
        assertEquals(PhotoStatus.NOT_FOUND, h.replies[1].payloadAs<PhotosThumbnailPayload>().status)
        h.lib.thumbBytes = ByteArray(PhotoService.MAX_THUMB_BYTES + 1)
        h.ask(thumb("t3", "1")); h.waitReplies(3)
        val big = h.replies[2].payloadAs<PhotosThumbnailPayload>()
        assertEquals(PhotoStatus.ERROR, big.status); assertNull(big.data)
        val json = h.replies[1].toJson()
        assertFalse("absent fields omitted: $json", json.contains("null"))
    }

    @Test
    fun fetch_announces_the_transfer_then_sends_the_copy() = runBlocking {
        val h = Harness(FakeLibrary(3))
        h.ask(fetch("f1", "3")); h.waitReplies(1)
        withTimeout(5_000) { while (h.sent.isEmpty()) delay(10) }
        val answer = h.replies[0].payloadAs<PhotosFetchedPayload>()
        assertEquals(PhotoStatus.OK, answer.status)
        val (file, name, transferId) = h.sent.single()
        assertEquals(answer.transferId, transferId)
        assertEquals("IMG_3.jpg", name)
        assertEquals("original IMG_3.jpg", file.readText())
        assertEquals(listOf("reply:${MessageType.PHOTOS_FETCHED}", "send"), h.order.toList()) // the PC learns the id first
        h.ask(fetch("f2", "42")); h.waitReplies(2)
        assertEquals(PhotoStatus.NOT_FOUND, h.replies[1].payloadAs<PhotosFetchedPayload>().status)
        assertNull(h.replies[1].payloadAs<PhotosFetchedPayload>().transferId)
    }

    @Test
    fun a_fetch_that_cannot_be_delivered_leaves_no_copy_behind() = runBlocking {
        val h = Harness(FakeLibrary(2))
        h.sendFails = true
        h.ask(fetch("a", "1")); h.waitReplies(1)
        withTimeout(5_000) { while (h.lib.exported.isEmpty() || h.lib.exported[0].exists()) delay(10) }
        h.sendFails = false; h.replyFails = true
        h.ask(fetch("b", "2"))
        withTimeout(5_000) { while (h.lib.exported.size < 2 || h.lib.exported[1].exists()) delay(10) }
        assertTrue("never sent when the PC can't be told", h.sent.isEmpty())
    }

    @Test
    fun too_many_waiting_requests_get_busy_and_the_rest_complete() = runBlocking {
        val h = Harness(FakeLibrary(10), maxQueued = 3)
        h.lib.gate = CountDownLatch(1)
        repeat(8) { h.ask(thumb("q$it", "1")) }
        h.waitReplies(5) // 5 refused at once
        assertEquals(5, h.replies.count { it.payloadAs<PhotosThumbnailPayload>().status == PhotoStatus.BUSY })
        h.lib.gate!!.countDown()
        h.waitReplies(8)
        assertEquals(3, h.replies.count { it.payloadAs<PhotosThumbnailPayload>().status == PhotoStatus.OK })
        h.lib.gate = null
        h.ask(thumb("after", "1")); h.waitReplies(9)
        assertEquals(PhotoStatus.OK, h.replies.last().payloadAs<PhotosThumbnailPayload>().status) // the queue drained
    }

    @Test
    fun permission_withdrawn_mid_request_or_library_errors_are_answered() = runBlocking {
        val h = Harness(FakeLibrary(2))
        h.lib.failWith = SecurityException("revoked")
        h.ask(list("s", 5)); h.waitReplies(1)
        assertEquals(PhotoStatus.NO_PERMISSION, h.replies[0].payloadAs<PhotosPagePayload>().status)
        h.lib.failWith = IllegalStateException("cursor closed")
        h.ask(list("e", 5)); h.waitReplies(2)
        assertEquals(PhotoStatus.ERROR, h.replies[1].payloadAs<PhotosPagePayload>().status)
    }

    @Test
    fun outcomes_are_reported_without_details() = runBlocking {
        val h = Harness(FakeLibrary(2))
        val seen = Collections.synchronizedList(mutableListOf<String>())
        h.service.onRequestHandled = { k, s -> seen += "$k:$s" }
        h.ask(list("1", 1)); h.ask(thumb("2", "x")); h.ask(fetch("3", "9"))
        h.waitReplies(3)
        assertEquals(setOf("list:ok", "thumb:invalid", "fetch:not_found"), seen.toSet())
    }

    @Test
    fun library_changes_are_debounced_into_one_hint() = runBlocking {
        val h = Harness(FakeLibrary(1))
        val hints = AtomicInteger()
        repeat(5) { h.service.libraryChanged { m -> assertEquals(MessageType.PHOTOS_CHANGED, m.type); hints.incrementAndGet() } ; delay(100) }
        delay(PhotoService.CHANGE_DEBOUNCE_MS + 600)
        assertEquals(1, hints.get())
        h.enabled = false
        h.service.libraryChanged { hints.incrementAndGet() }
        delay(PhotoService.CHANGE_DEBOUNCE_MS + 300)
        assertEquals("nothing while photos are off", 1, hints.get())
    }

    @Test
    fun turning_photos_on_or_off_always_tells_the_pc() = runBlocking {
        val h = Harness(FakeLibrary(1), enabled = false)
        val hints = AtomicInteger()
        h.service.settingChanged { hints.incrementAndGet() } // just switched off: the PC must stop showing photos
        delay(PhotoService.CHANGE_DEBOUNCE_MS + 500)
        assertEquals(1, hints.get())
    }

    @Test
    fun cursor_round_trips_and_rejects_junk() {
        assertEquals(PhotoCursor(1700000000000, 42), PhotoCursor.parse(PhotoCursor(1700000000000, 42).encode()))
        for (bad in listOf(null, "", "1", "a:1", "1:b", "1:2:3", " 1:2", "1:2 ", "-1:2", "1".repeat(16) + ":1", "1:" + "9".repeat(20)))
            assertNull("$bad", PhotoCursor.parse(bad))
    }

    // ------------------------------------------------------------------ over a real session

    private object NoClipboard : ClipboardAccess {
        override fun readText(): String? = null
        override fun writeText(text: String) {}
        override fun setChangeListener(listener: (String) -> Unit) {}
    }

    @Test
    fun session_offers_photos_and_delivers_a_fetched_original() = runBlocking {
        val lib = FakeLibrary(4)
        val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
        val service = PhotoService(lib, { true }, scope)
        val result = CompletableDeferred<String>()
        TestPc {
            handshake()
            val features = receive().payloadAs<CapabilitiesPayload>().features
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES, Features.FILES_RESUME, Features.PHOTOS))))
            send(list("L", 2))
            send(fetch("F", "4"))
            var page: PhotosPagePayload? = null
            var announced: String? = null
            var started: FileStartPayload? = null
            val data = java.io.ByteArrayOutputStream()
            while (true) {
                val m = receive()
                when (m.type) {
                    MessageType.PHOTOS_PAGE -> page = m.payloadAs()
                    MessageType.PHOTOS_FETCHED -> announced = m.payloadAs<PhotosFetchedPayload>().transferId
                    MessageType.FILE_START -> {
                        started = m.payloadAs()
                        check(announced == started!!.transferId) { "file.start before photos.fetched, or a different id" }
                        send(BridgeMessage.create(MessageType.FILE_RESUME, FileResumePayload(started!!.transferId, 0)))
                    }
                    MessageType.FILE_CHUNK -> data.write(Base64.getDecoder().decode(m.payloadAs<FileChunkPayload>().data))
                    MessageType.FILE_END -> {
                        val ok = FileHasher.sha256Hex(data.toByteArray()).equals(started!!.sha256, true)
                        send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(started!!.transferId, if (ok) "ok" else "hash_mismatch")))
                        result.complete("${Features.PHOTOS in features}|${page?.items?.map { it.id }}|${page?.next != null}|${started!!.fileName}|${String(data.toByteArray())}")
                        break
                    }
                }
            }
            ignoreRest()
        }.use { pc ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                FileTransferClient {}, ClipboardSyncClient(NoClipboard, this), Files.createTempDirectory("dl").toFile(), photos = service)
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            assertEquals("true|[4, 3]|true|IMG_4.jpg|original IMG_4.jpg", withTimeout(15_000) { result.await() })
            withTimeout(5_000) { while (lib.exported.single().exists()) delay(20) } // the private copy is deleted once delivered
            s.close(); job.cancelAndJoin(); scope.cancel()
        }
    }

    @Test
    fun without_a_photo_handler_the_phone_does_not_offer_photos() = runBlocking {
        val features = CompletableDeferred<List<String>>()
        TestPc {
            handshake(); features.complete(receive().payloadAs<CapabilitiesPayload>().features)
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(listOf(Features.FILES))))
            ignoreRest()
        }.use { pc ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                FileTransferClient {}, ClipboardSyncClient(NoClipboard, this), Files.createTempDirectory("dl").toFile())
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            assertFalse(Features.PHOTOS in withTimeout(10_000) { features.await() })
            s.close(); job.cancelAndJoin()
        }
    }
}
