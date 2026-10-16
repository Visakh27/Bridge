package com.bridge.app

import com.bridge.app.core.DiagnosticsLog
import com.bridge.app.core.DiagnosticsReport
import com.bridge.app.core.LogField
import com.bridge.app.core.PhoneDiagnostics
import com.bridge.app.network.*
import com.bridge.app.notifications.*
import com.bridge.app.protocol.*
import java.io.File
import java.io.IOException
import java.nio.file.Files
import java.util.Base64
import kotlinx.coroutines.*
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertNotEquals
import org.junit.Assert.assertTrue
import org.junit.Test

/** Phase D on the phone: diagnostics log, recent transfers, retry copies, reconnect feedback. */
class PolishTest {
    private val line = Regex("^\\d{4}-\\d\\d-\\d\\dT\\d\\d:\\d\\d:\\d\\d(\\.\\d+)?Z (INFO|WARN) [a-z][a-z0-9_.]*( [A-Za-z_][A-Za-z0-9_-]*=[^ ]+)*$")
    private fun tmp() = Files.createTempDirectory("bridge").toFile()

    @Test
    fun only_safe_shapes_reach_the_log() {
        val log = DiagnosticsLog(null)
        log.info("transfer.done",
            LogField.code("status", "COMPLETED"),
            LogField.code("name", "holiday photos.zip"),
            LogField.code("name2", "report.pdf"),
            LogField.code("long", "a".repeat(40)),
            LogField.code("nul", null),
            LogField.id("peer", "phone-secret-id", log),
            LogField.num("chunks", 12),
            LogField.flag("folder", true),
            LogField.error(IOException("/sdcard/secret.txt locked")),
            LogField.version("version", "0.2.0"),
            LogField.version("bad_version", "0.2.0-secret"))
        log.info("Bad Event", LogField.num("bad key", 1), LogField.num("ok", 2))
        val (first, second) = log.lines()
        for (part in listOf("status=COMPLETED", "name=? ", "name2=? ", "long=? ", "nul=? ", "chunks=12", "folder=true", "error=IOException", "version=0.2.0", "bad_version=?"))
            assertTrue("$part in $first", first.contains(part))
        assertTrue(Regex("peer=[0-9a-f]{8} ").containsMatchIn(first))
        assertFalse(first.contains("secret") || first.contains("holiday"))
        assertTrue(second.endsWith(" invalid_event ok=2"))
        assertEquals(log.hashId("x"), log.hashId("x"))
        assertNotEquals(log.hashId("x"), DiagnosticsLog(null).hashId("x"))
        assertTrue(log.lines().all { line.matches(it) })
    }

    @Test
    fun log_rotates_and_never_grows_past_its_cap() {
        val dir = tmp()
        val log = DiagnosticsLog(dir)
        repeat(40_000) { log.info("transfer.done", LogField.num("i", it), LogField.code("status", "COMPLETED")) }
        val files = log.logFiles()
        assertTrue("${files.size} files", files.size <= DiagnosticsLog.FILES)
        assertTrue(files.all { it.length() <= DiagnosticsLog.MAX_FILE_BYTES })
        assertEquals(DiagnosticsLog.FILES, dir.listFiles()!!.size)
        assertTrue(log.lines().last().endsWith("i=39999 status=COMPLETED"))
        assertTrue(log.lines().all { line.matches(it) })
    }

    @Test
    fun old_logs_are_deleted_and_disabled_writes_nothing() {
        val dir = tmp()
        File(dir, "bridge.1.log").apply { writeText("old\n"); setLastModified(System.currentTimeMillis() - DiagnosticsLog.RETENTION_MS - 60_000) }
        File(dir, "bridge.log").writeText("recent\n")
        val log = DiagnosticsLog(dir)
        assertEquals(listOf("recent"), log.lines())
        log.enabled = false
        log.info("app.started")
        assertEquals(listOf("recent"), log.lines())
        log.clear()
        assertTrue(log.lines().isEmpty())
        assertTrue(dir.listFiles()!!.isEmpty())
    }

    @Test
    fun concurrent_writers_never_interleave_lines() {
        val log = DiagnosticsLog(tmp())
        (0 until 8).map { t -> Thread { repeat(1500) { log.info("transfer.done", LogField.num("t", t), LogField.num("i", it)) } } }
            .onEach { it.start() }.forEach { it.join() }
        val lines = log.lines()
        assertTrue(lines.size > 1000)
        assertTrue(lines.firstOrNull { !line.matches(it) } ?: "", lines.all { line.matches(it) })
    }

    @Test
    fun unwritable_log_directory_never_throws() {
        val notDir = File(tmp(), "file").apply { writeText("x") }
        val log = DiagnosticsLog(notDir)
        log.info("app.started")
        assertTrue(log.lines().isEmpty())
    }

    @Test
    fun phone_events_and_report_carry_no_names_or_contents() {
        val secret = "ZEBRA"
        val log = DiagnosticsLog(tmp())
        PhoneDiagnostics.connectAttempt(log, "pc-$secret")
        PhoneDiagnostics.connectFailed(log, IOException("connect to 10.0.0.5:47820 ($secret) failed"))
        PhoneDiagnostics.sessionStarted(log, "pc-$secret", listOf("files", "notifications.actions"))
        PhoneDiagnostics.transfer(log, TransferProgress("t-$secret", "$secret.pdf", TransferDirection.SENDING, TransferStatus.IN_PROGRESS))
        PhoneDiagnostics.transfer(log, TransferProgress("t-$secret", "$secret.pdf", TransferDirection.SENDING, TransferStatus.FAILED,
            totalChunks = 3, sourceFile = File("/cache/$secret"), retainedCopy = true, relativePath = "$secret/x"))
        PhoneDiagnostics.sessionEnded(log, "pc-$secret", 1234)
        val text = log.lines().joinToString("\n")
        for (part in listOf("connect.attempt", "connect.failed error=IOException", "session.started", "features=2 actions=true",
                "transfer.done", "dir=SENDING status=FAILED chunks=3", "kept_for_retry=true", "session.ended", "duration_ms=1234"))
            assertTrue("$part in\n$text", text.contains(part))
        assertEquals("only outcomes are logged", 1, log.lines().count { it.contains("transfer.done") })
        assertFalse(text.contains(secret, ignoreCase = true))
        val report = DiagnosticsReport.build(log, 0L, LogField.version("app_version", "0.2.0"), LogField.flag("clipboard_sync", true))
        assertTrue(report.startsWith("Bridge diagnostics report"))
        assertTrue(report.contains("app_version=0.2.0") && report.contains("clipboard_sync=true"))
        assertFalse(report.contains(secret, ignoreCase = true))
    }

    // ------------------------------------------------------------------ recent transfers

    private fun p(id: String, dir: TransferDirection, status: TransferStatus, source: File? = null, saved: File? = null,
                  rel: String? = null, retained: Boolean = false) =
        TransferProgress(id, "$id.bin", dir, status, chunksDone = 2, totalChunks = 4, savedFile = saved, relativePath = rel,
            sourceFile = source, retainedCopy = retained)

    @Test
    fun recent_transfers_track_updates_newest_first_and_cap() {
        val h = TransferHistory()
        repeat(25) { h.update(p("t$it", TransferDirection.SENDING, TransferStatus.COMPLETED)) }
        assertEquals(20, h.entries.size)
        assertEquals("t24", h.entries.first().transferId)
        h.update(p("t10", TransferDirection.SENDING, TransferStatus.IN_PROGRESS))
        assertEquals("t10", h.entries.first().transferId)
        assertEquals(1, h.entries.count { it.transferId == "t10" })
        h.update(p("t10", TransferDirection.RECEIVING, TransferStatus.IN_PROGRESS))
        assertEquals(2, h.entries.count { it.transferId == "t10" })
        h.update(p("paused", TransferDirection.SENDING, TransferStatus.INTERRUPTED))
        h.clearFinished()
        assertTrue(h.entries.all { it.status == TransferStatus.IN_PROGRESS || it.status == TransferStatus.INTERRUPTED })
        h.remove("paused")
        assertFalse(h.entries.any { it.transferId == "paused" })
        assertEquals("Sending · 50%", TransferHistory.describe(h.entries.first { it.direction == TransferDirection.SENDING }))
    }

    @Test
    fun retry_and_open_only_when_they_can_work_and_clearing_frees_kept_copies() {
        val dir = tmp()
        val src = File(dir, "a").apply { writeText("x") }
        val kept = File(dir, "copy").apply { writeText("x") }
        val saved = File(dir, "b").apply { writeText("y") }
        val h = TransferHistory()
        h.update(p("fail", TransferDirection.SENDING, TransferStatus.FAILED, src))
        h.update(p("refused", TransferDirection.SENDING, TransferStatus.CANCELLED, kept, retained = true))
        h.update(p("ok", TransferDirection.SENDING, TransferStatus.COMPLETED, src))
        h.update(p("gone", TransferDirection.SENDING, TransferStatus.FAILED, File(dir, "deleted")))
        h.update(p("infolder", TransferDirection.SENDING, TransferStatus.FAILED, src, rel = "Album/2026"))
        h.update(p("got", TransferDirection.RECEIVING, TransferStatus.COMPLETED, saved = saved))
        h.update(p("got-moved", TransferDirection.RECEIVING, TransferStatus.COMPLETED, saved = File(dir, "moved")))
        h.update(p("fail", TransferDirection.SENDING, TransferStatus.FAILED)) // keeps the source seen before
        val e = h.entries.associateBy { it.transferId }
        assertTrue(e.getValue("fail").canRetry)
        assertTrue(e.getValue("refused").canRetry)
        assertFalse(e.getValue("ok").canRetry)
        assertFalse(e.getValue("gone").canRetry)
        assertFalse(e.getValue("infolder").canRetry)
        assertEquals("Album", e.getValue("infolder").folder)
        assertTrue(e.getValue("got").canOpen)
        assertFalse(e.getValue("got-moved").canOpen)
        assertEquals(listOf(kept), h.clearFinished()) // only Bridge's own copies are handed back for deleting
    }

    // ------------------------------------------------------------------ retry copies

    private object NoClipboard : ClipboardAccess {
        override fun readText(): String? = null
        override fun writeText(text: String) {}
        override fun setChangeListener(listener: (String) -> Unit) {}
    }

    /** Sends a private copy to a PC that answers [answer] to the file; returns (final progress, copy still exists). */
    private suspend fun CoroutineScope.sendCopy(retain: Boolean, answer: String?, userCancel: Boolean = false): Pair<TransferProgress, Boolean> {
        val dir = tmp()
        val copy = File(dir, "share-copy").apply { writeBytes(ByteArray(FileTransferClient.CHUNK_SIZE * 3)) }
        val files = FileTransferClient {}.apply { retainFailedCopies = retain }
        val started = CompletableDeferred<String>()
        return TestPc {
            handshake(); receive()
            // Waiting for a reply the PC never sends (resumable) gives the user time to cancel.
            val features = if (userCancel) listOf(Features.FILES, Features.FILES_RESUME) else listOf(Features.FILES)
            send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(features)))
            val start = receive().payloadAs<FileStartPayload>()
            started.complete(start.transferId)
            if (answer != null) send(BridgeMessage.create(MessageType.FILE_ACK, FileAckPayload(start.transferId, answer)))
            ignoreRest()
        }.use { pc ->
            val s = BridgeClientSession(SoftwareIdentity(), PairedDevice(pc.identity.deviceId, "PC", pc.publicKeyBase64(), "127.0.0.1", pc.port),
                files, ClipboardSyncClient(NoClipboard, this), dir)
            val job = launch(Dispatchers.IO) { runCatching { s.run() } }
            withTimeout(10_000) { while (!s.isConnected) delay(10) }
            val sending = async { s.sendFile(copy, "photo.jpg", deleteWhenDone = true)!! }
            val id = withTimeout(10_000) { started.await() }
            if (userCancel) files.cancelTransfer(id)
            val result = withTimeout(10_000) { sending.await() }
            s.close(); job.cancelAndJoin()
            result to copy.exists()
        }
    }

    @Test
    fun a_refused_send_keeps_its_private_copy_for_retry() = runBlocking {
        val (refused, kept) = sendCopy(retain = true, answer = FileAckStatus.CANCELLED)
        assertEquals(TransferStatus.CANCELLED, refused.status)
        assertTrue(refused.retainedCopy && kept)
        assertTrue(refused.sourceFile!!.name == "share-copy")
        val (failed, keptFailed) = sendCopy(retain = true, answer = FileAckStatus.HASH_MISMATCH)
        assertEquals(TransferStatus.FAILED, failed.status)
        assertTrue(failed.retainedCopy && keptFailed)
    }

    @Test
    fun copies_are_deleted_when_not_retained_or_cancelled_by_the_user() = runBlocking {
        val (r1, kept1) = sendCopy(retain = false, answer = FileAckStatus.CANCELLED)
        assertFalse(r1.retainedCopy || kept1)
        val (r2, kept2) = sendCopy(retain = true, answer = null, userCancel = true)
        assertEquals(TransferStatus.CANCELLED, r2.status)
        assertFalse("the user cancelled: nothing to retry", r2.retainedCopy || kept2)
    }

    @Test
    fun stale_copies_are_swept_but_queued_and_fresh_ones_kept() {
        val dir = tmp()
        val old = System.currentTimeMillis() - FileTransferClient.RETAINED_COPY_MS - 60_000
        val stale = File(dir, "stale").apply { writeText("x"); setLastModified(old) }
        val queued = File(dir, "queued").apply { writeText("x"); setLastModified(old) }
        val fresh = File(dir, "fresh").apply { writeText("x") }
        val sub = File(dir, "sub").apply { mkdirs(); setLastModified(old) }
        assertEquals(1, FileTransferClient.sweepStaleCopies(dir, setOf(queued.path)))
        assertFalse(stale.exists())
        assertTrue(queued.exists() && fresh.exists() && sub.exists())
        assertEquals(0, FileTransferClient.sweepStaleCopies(File(dir, "missing"), emptySet()))
    }

    @Test
    fun outbox_paths_are_reported_for_the_sweep() {
        val store = TransferStateStore(tmp())
        store.addOutbox(TransferStateStore.OutboxEntry("a", "pc", "/cache/share/1", "x.jpg", created = System.currentTimeMillis()))
        store.addOutbox(TransferStateStore.OutboxEntry("b", "pc2", "/cache/share/2", "y.jpg", created = System.currentTimeMillis()))
        assertEquals(setOf("/cache/share/1", "/cache/share/2"), store.pendingPaths())
    }

    // ------------------------------------------------------------------ reconnect feedback

    private class MemStore(vararg initial: PairedDevice) : PairedDeviceStore {
        val devices = initial.toMutableList()
        override suspend fun save(device: PairedDevice) { devices.removeAll { it.deviceId == device.deviceId }; devices.add(device) }
        override suspend fun list() = devices.toList()
        override suspend fun revoke(deviceId: String) { devices.removeAll { it.deviceId == deviceId } }
    }

    @Test
    fun reconnect_loop_reports_failures_and_the_next_attempt() = runBlocking {
        val pc = SoftwareIdentity()
        val paired = PairedDevice(pc.deviceId, "PC", Base64.getEncoder().encodeToString(pc.publicKeyBytes()), "127.0.0.1", 1)
        val failures = mutableListOf<String>()
        val waits = mutableListOf<Long>()
        var attempts = 0
        val loop = ReconnectLoop(MemStore(paired), null, { _ ->
            object : SessionHandle {
                override suspend fun run() { attempts++; throw IOException("unreachable") }
                override fun close() {}
            }
        }, listener = object : ReconnectLoop.Listener {
            override fun onAttemptFailed(pc: PairedDevice, error: Exception) { synchronized(failures) { failures += error.javaClass.simpleName } }
            override fun onRetryScheduled(delayMs: Long) { synchronized(waits) { waits += delayMs } }
        }, backoffMs = listOf(50, 60_000))
        val job = launch { loop.run() }
        withTimeout(5_000) { while (synchronized(waits) { waits.size } < 2) delay(10) }
        assertEquals(listOf(50L, 60_000L), synchronized(waits) { waits.toList() })
        loop.wake() // "Try now"
        withTimeout(5_000) { while (attempts < 3) delay(10) }
        job.cancelAndJoin()
        assertTrue(synchronized(failures) { failures.toList() }.all { it == "IOException" })
        assertTrue(failures.size >= 3)
    }

    // ------------------------------------------------------------------ notification request outcomes

    @Test
    fun notification_requests_report_their_outcome_without_details() = runBlocking {
        val shown = mutableMapOf<String, PhoneNotification>()
        val source = object : NotificationSource {
            override fun find(key: String) = shown[key]
            override fun active() = shown.values.toList()
            override fun cancel(key: String) = shown.remove(key) != null
        }
        val m = NotificationMirror(NotificationFilter(InMemoryNotificationFilterStore()), source, "com.bridge.app")
        val seen = mutableListOf<String>()
        m.onRequestHandled = { kind, status -> seen += "$kind:$status" }
        shown["k"] = PhoneNotification("k", "com.chat", "Chat", "T", "x", actions = listOf(PhoneAction("Reply", true) { true }))
        val id = NotificationMirror.actionId(0, shown.getValue("k").actions[0])
        m.handleIncoming(BridgeMessage.create(MessageType.NOTIFICATION_ACTION, NotificationActionPayload("r", "k", id, "hi"))) {}
        m.handleIncoming(BridgeMessage.create(MessageType.NOTIFICATION_ACTION, NotificationActionPayload("r", "gone", id))) {}
        m.handleIncoming(BridgeMessage.create(MessageType.NOTIFICATION_DISMISS, NotificationDismissPayload("k"))) {}
        m.handleIncoming(BridgeMessage.create(MessageType.NOTIFICATION_MUTE, NotificationMutePayload("com.chat"))) {}
        m.handleIncoming(BridgeMessage.create(MessageType.NOTIFICATION_MUTE, NotificationMutePayload(""))) {}
        assertEquals(listOf("reply:ok", "press:gone", "dismiss:ok", "mute:ok", "mute:invalid"), seen)
    }
}
