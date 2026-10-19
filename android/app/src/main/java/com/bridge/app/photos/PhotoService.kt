package com.bridge.app.photos

import com.bridge.app.protocol.*
import java.io.File
import java.util.UUID
import java.util.concurrent.atomic.AtomicInteger
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.launch
import kotlinx.coroutines.sync.Semaphore
import kotlinx.coroutines.sync.withPermit

/** Position in the newest-first photo list: everything before (sortKey, id). */
data class PhotoCursor(val sortKey: Long, val id: Long) {
    fun encode() = "$sortKey:$id"
    companion object {
        private val FORMAT = Regex("^(\\d{1,15}):(\\d{1,19})$")
        fun parse(s: String?): PhotoCursor? = s?.let { FORMAT.matchEntire(it) }?.let { m ->
            val t = m.groupValues[1].toLongOrNull(); val i = m.groupValues[2].toLongOrNull()
            if (t != null && i != null) PhotoCursor(t, i) else null
        }
    }
}

/**
 * A photo as the library lists it. [sortKey] orders the list (newest first; on
 * Android the time it was added, which every photo has) and may differ from
 * [info].takenAt, which is what the PC shows.
 */
data class LibraryPhoto(val info: PhotoInfo, val sortKey: Long)

/** A photo copied out of the library so it can be sent like any file. */
data class ExportedPhoto(val file: File, val name: String)

/** The phone's photo library (MediaStore on a device). Only ids it returned can be read back. */
interface PhotoLibrary {
    fun hasPermission(): Boolean
    /** Newest first by (sortKey, id), strictly before [before]; at most [limit]. */
    fun page(before: PhotoCursor?, limit: Int): List<LibraryPhoto>
    /** A JPEG at most [maxSide] px on its long side, or null if there's no such photo. */
    fun thumbnail(id: Long, maxSide: Int): ByteArray?
    /** Copies the original into private cache, or null if there's no such photo. */
    fun export(id: Long): ExportedPhoto?
}

/** Handles the PC's photos.* requests on behalf of BridgeClientSession. */
interface PhotoRequestHandler {
    /**
     * [reply] sends on the session; [sendFile] starts a normal file transfer
     * with the given id (the copy is deleted when it's done).
     */
    suspend fun handleIncoming(message: BridgeMessage, reply: suspend (BridgeMessage) -> Unit,
                               sendFile: suspend (file: File, name: String, transferId: String) -> Unit)
}

/**
 * The phone side of photo browsing. Off until the user turns on "Let your PC
 * browse photos" ([enabled]); then the PC can list photos newest first, get
 * small previews and fetch originals — always by library id, never by path.
 * Work runs off the session's receive loop, a few at a time; when more than
 * [maxQueued] requests are waiting the PC is told "busy" instead of piling up.
 */
class PhotoService(
    private val library: PhotoLibrary,
    private val enabled: () -> Boolean,
    private val scope: CoroutineScope,
    private val maxQueued: Int = 64,
    workers: Int = 2,
) : PhotoRequestHandler {
    companion object {
        const val MAX_PAGE = 100
        const val THUMB_SIDE = 320
        const val MAX_THUMB_BYTES = 256 * 1024
        const val CHANGE_DEBOUNCE_MS = 2_000L
        private val ID = Regex("^\\d{1,19}$")
        private const val MAX_REQUEST_ID = 64
    }

    private val work = Semaphore(workers)
    private val queued = AtomicInteger(0)

    /** Diagnostics: (request kind, status) — never ids or names. */
    @Volatile var onRequestHandled: ((kind: String, status: String) -> Unit)? = null

    /** What a request produced: its [status], the [answer] to send, and (fetch) what to do after answering. */
    private class Outcome(val status: String, val answer: BridgeMessage, val then: (suspend () -> Unit)? = null, val discard: (() -> Unit)? = null)

    override suspend fun handleIncoming(message: BridgeMessage, reply: suspend (BridgeMessage) -> Unit,
                                        sendFile: suspend (File, String, String) -> Unit) {
        when (message.type) {
            MessageType.PHOTOS_LIST -> {
                val req = message.payloadAs<PhotosListPayload>()
                if (!validRequestId(req.requestId)) return
                fun answer(status: String, items: List<PhotoInfo> = emptyList(), next: String? = null) =
                    Outcome(status, BridgeMessage.create(MessageType.PHOTOS_PAGE, PhotosPagePayload(req.requestId, status, items, next)))
                val before = if (req.before == null) null else PhotoCursor.parse(req.before)
                if (req.limit < 1 || (req.before != null && before == null)) { send("list", reply, answer(PhotoStatus.INVALID)); return }
                enqueue("list", reply, { answer(it) }) {
                    val limit = minOf(req.limit, MAX_PAGE)
                    val items = library.page(before, limit + 1) // one extra tells us whether there's more
                    val pageItems = items.take(limit)
                    val next = if (items.size > limit) pageItems.last().let { PhotoCursor(it.sortKey, it.info.id.toLong()).encode() } else null
                    answer(PhotoStatus.OK, pageItems.map { it.info }, next)
                }
            }
            MessageType.PHOTOS_THUMB -> {
                val req = message.payloadAs<PhotosThumbPayload>()
                if (!validRequestId(req.requestId)) return
                fun answer(status: String, data: ByteArray? = null) = Outcome(status, BridgeMessage.create(MessageType.PHOTOS_THUMBNAIL,
                    PhotosThumbnailPayload(req.requestId, req.id, status, data?.let { "image/jpeg" }, data?.let { java.util.Base64.getEncoder().encodeToString(it) })))
                val id = parseId(req.id) ?: run { send("thumb", reply, answer(PhotoStatus.INVALID)); return }
                enqueue("thumb", reply, { answer(it) }) {
                    val bytes = library.thumbnail(id, THUMB_SIDE)
                    when {
                        bytes == null -> answer(PhotoStatus.NOT_FOUND)
                        bytes.size > MAX_THUMB_BYTES -> answer(PhotoStatus.ERROR)
                        else -> answer(PhotoStatus.OK, bytes)
                    }
                }
            }
            MessageType.PHOTOS_FETCH -> {
                val req = message.payloadAs<PhotosFetchPayload>()
                if (!validRequestId(req.requestId)) return
                fun answer(status: String, transferId: String? = null) = BridgeMessage.create(MessageType.PHOTOS_FETCHED,
                    PhotosFetchedPayload(req.requestId, req.id, status, transferId))
                val id = parseId(req.id) ?: run { send("fetch", reply, Outcome(PhotoStatus.INVALID, answer(PhotoStatus.INVALID))); return }
                enqueue("fetch", reply, { Outcome(it, answer(it)) }) {
                    val exported = library.export(id) ?: return@enqueue Outcome(PhotoStatus.NOT_FOUND, answer(PhotoStatus.NOT_FOUND))
                    val transferId = UUID.randomUUID().toString()
                    // Announce the transfer first, so the PC knows which incoming file is this photo.
                    Outcome(PhotoStatus.OK, answer(PhotoStatus.OK, transferId),
                        then = { sendFile(exported.file, exported.name, transferId) },
                        discard = { exported.file.delete() })
                }
            }
        }
    }

    private suspend fun send(kind: String, reply: suspend (BridgeMessage) -> Unit, outcome: Outcome): Boolean {
        onRequestHandled?.invoke(kind, outcome.status)
        return runCatching { reply(outcome.answer) }.isSuccess
    }

    /**
     * Runs [job] on a worker unless photos are switched off or there's no
     * permission (answered right away), or too much is queued ("busy").
     */
    private suspend fun enqueue(kind: String, reply: suspend (BridgeMessage) -> Unit, failure: (String) -> Outcome, job: suspend () -> Outcome) {
        if (!enabled()) { send(kind, reply, failure(PhotoStatus.DISABLED)); return }
        if (!library.hasPermission()) { send(kind, reply, failure(PhotoStatus.NO_PERMISSION)); return }
        if (queued.incrementAndGet() > maxQueued) {
            queued.decrementAndGet()
            send(kind, reply, failure(PhotoStatus.BUSY))
            return
        }
        scope.launch {
            try {
                val outcome = work.withPermit {
                    try {
                        job()
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: SecurityException) {
                        failure(PhotoStatus.NO_PERMISSION) // access withdrawn in Android settings meanwhile
                    } catch (e: Exception) {
                        failure(PhotoStatus.ERROR)
                    }
                }
                val delivered = send(kind, reply, outcome)
                val then = outcome.then
                if (then == null) return@launch
                if (!delivered) { outcome.discard?.invoke(); return@launch }
                try { then() } catch (e: CancellationException) { outcome.discard?.invoke(); throw e } catch (e: Exception) { outcome.discard?.invoke() }
            } finally {
                queued.decrementAndGet()
            }
        }
    }

    private fun validRequestId(id: String?) = !id.isNullOrEmpty() && id.length <= MAX_REQUEST_ID
    private fun parseId(id: String?): Long? = id?.takeIf { ID.matches(it) }?.toLongOrNull()

    // ------------------------------------------------------------------ change hints

    private var changeJob: Job? = null
    private val changeLock = Any()

    /**
     * The library changed (a photo was taken or deleted). After [CHANGE_DEBOUNCE_MS]
     * of quiet, [notify] sends photos.changed so the PC refreshes — never more often.
     */
    fun libraryChanged(notify: suspend (BridgeMessage) -> Unit) {
        if (!enabled()) return
        schedule(notify)
    }

    /** The user turned photos on or off (or changed access): the PC's view must refresh either way. */
    fun settingChanged(notify: suspend (BridgeMessage) -> Unit) = schedule(notify)

    private fun schedule(notify: suspend (BridgeMessage) -> Unit) {
        synchronized(changeLock) {
            changeJob?.cancel()
            changeJob = scope.launch {
                delay(CHANGE_DEBOUNCE_MS)
                runCatching { notify(BridgeMessage.createEmpty(MessageType.PHOTOS_CHANGED)) }
            }
        }
    }
}
