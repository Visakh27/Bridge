package com.bridge.app.handoff

import com.bridge.app.protocol.*
import java.net.IDN
import java.net.URI
import java.util.UUID
import java.util.concurrent.ConcurrentHashMap
import kotlinx.coroutines.CompletableDeferred
import kotlinx.coroutines.withTimeoutOrNull

/** A link checked and ready to show: [host] is ASCII (punycode), so look-alike domains can't hide. */
data class HandoffLink(val url: String, val host: String, val title: String?)

/**
 * What may be handed over — the same rules as Bridge.Core.HandoffRules on
 * Windows: plain http(s) links without credentials, at most [MAX_URL]
 * characters; text up to [MAX_TEXT]; titles cleaned for display.
 */
object HandoffRules {
    const val MAX_URL = 2048
    const val MAX_TEXT = 10_000
    const val MAX_TITLE = 200
    const val MAX_ID = 64
    private val PORT = Regex("^(:[0-9]{1,5})?$")

    fun checkUrl(raw: String?, title: String? = null): HandoffLink? {
        val url = raw?.trim() ?: return null
        if (url.isEmpty() || url.length > MAX_URL || url.any { it.isISOControl() || it.isWhitespace() }) return null
        val uri = try { URI(url) } catch (e: Exception) { return null }
        val scheme = uri.scheme?.lowercase() ?: return null
        if (scheme != "http" && scheme != "https") return null
        if (uri.rawUserInfo != null) return null
        // java.net.URI leaves non-ASCII hosts unparsed: take them from the authority.
        val authority = uri.rawAuthority ?: return null
        if (authority.contains('@')) return null
        val hostPart = uri.host ?: authority.substringBeforeLast(':').takeIf { !authority.startsWith("[") } ?: return null
        if (hostPart.isEmpty()) return null
        val host = if (hostPart.startsWith("[")) hostPart.lowercase() else try {
            IDN.toASCII(hostPart, IDN.USE_STD3_ASCII_RULES).lowercase()
        } catch (e: IllegalArgumentException) { return null }
        if (host.isEmpty()) return null
        val port = authority.substring(hostPart.length.coerceAtMost(authority.length))
        if (!PORT.matches(port)) return null
        // Rebuilt around the ASCII host, so what's sent and opened is exactly the host that's shown.
        val rest = url.substring(url.indexOf("//") + 2 + authority.length)
        val normalized = "$scheme://$host$port$rest"
        return if (normalized.length > MAX_URL) null else HandoffLink(normalized, host, cleanTitle(title))
    }

    fun checkText(text: String?): String? =
        if (text == null || text.isBlank() || text.length > MAX_TEXT || text.contains('\u0000')) null else text

    private val BIDI = Regex("[\\u202A-\\u202E\\u2066-\\u2069]")

    fun cleanTitle(title: String?): String? {
        val t = title?.replace(BIDI, "")?.map { if (it == '\n' || it == '\r' || it == '\t') ' ' else it }
            ?.filter { !it.isISOControl() }?.joinToString("")?.trim()
        if (t.isNullOrEmpty()) return null
        return if (t.length <= MAX_TITLE) t else t.take(MAX_TITLE - 1).trimEnd() + "…"
    }

    /** A one-line preview of handed-over text. */
    fun preview(text: String, max: Int = 80): String = cleanTitle(text)?.let { if (it.length <= max) it else it.take(max).trimEnd() + "…" } ?: ""
}

/** Answers the PC's handoff messages on behalf of BridgeClientSession. */
interface HandoffHandler {
    suspend fun handleIncoming(message: BridgeMessage, reply: suspend (BridgeMessage) -> Unit)
}

/**
 * The phone side of handoff. Incoming: checks links/text ([HandoffRules]),
 * answers repeats of an id with the same answer without showing anything
 * again, refuses floods (more than [MAX_PER_MINUTE] a minute), and passes
 * what's left to [onLink] (a notification — the phone never opens a link by
 * itself) or [onText] (the clipboard). Outgoing: [sendUrl] / [sendText] wait
 * for the PC's answer.
 */
class HandoffInbox(
    private val enabled: () -> Boolean,
    private val onLink: (HandoffLink) -> Unit,
    private val onText: (String) -> Unit,
    private val now: () -> Long = System::currentTimeMillis,
) : HandoffHandler {
    companion object {
        const val MAX_PER_MINUTE = 20
        const val ACK_TIMEOUT_MS = 10_000L

        /** A status the PC may send; anything else (or nothing) counts as invalid. */
        fun wireStatus(status: String?): String = when (status) {
            HandoffStatus.SHOWN, HandoffStatus.OPENED, HandoffStatus.COPIED, HandoffStatus.DISABLED, HandoffStatus.BUSY -> status
            else -> HandoffStatus.INVALID
        }
    }

    private val lock = Any()
    private val recent = ArrayDeque<Long>()
    private val seen = LinkedHashMap<String, String>()
    private val pending = ConcurrentHashMap<String, CompletableDeferred<String>>()

    /** Diagnostics: (direction, kind, status) — never the link or text. */
    @Volatile var onCompleted: ((String, String, String) -> Unit)? = null

    override suspend fun handleIncoming(message: BridgeMessage, reply: suspend (BridgeMessage) -> Unit) {
        when (message.type) {
            MessageType.HANDOFF_ACK -> {
                val ack = runCatching { message.payloadAs<HandoffAckPayload>() }.getOrNull() ?: return
                pending[ack.id]?.complete(wireStatus(ack.status))
            }
            MessageType.HANDOFF_OPEN -> {
                val p = runCatching { message.payloadAs<HandoffPayload>() }.getOrNull() ?: return
                if (p.id.isEmpty() || p.id.length > HandoffRules.MAX_ID) return
                val status = decide(p)
                onCompleted?.invoke("in", if (p.kind == HandoffKind.URL || p.kind == HandoffKind.TEXT) p.kind else "unknown", status)
                runCatching { reply(BridgeMessage.create(MessageType.HANDOFF_ACK, HandoffAckPayload(p.id, status))) }
            }
        }
    }

    private fun decide(p: HandoffPayload): String {
        // One at a time, start to finish: two copies of an id arriving together still act once.
        synchronized(lock) {
            seen[p.id]?.let { return it } // a retry: same answer, nothing shown twice
            val status = decideNew(p)
            seen[p.id] = status
            if (seen.size > 100) seen.remove(seen.keys.first())
            return status
        }
    }

    private fun decideNew(p: HandoffPayload): String {
        if (!enabled()) return HandoffStatus.DISABLED
        return when (p.kind) {
            HandoffKind.URL -> {
                val link = HandoffRules.checkUrl(p.url, p.title) ?: return HandoffStatus.INVALID
                if (!admit()) return HandoffStatus.BUSY
                onLink(link); HandoffStatus.SHOWN
            }
            HandoffKind.TEXT -> {
                val text = HandoffRules.checkText(p.text) ?: return HandoffStatus.INVALID
                if (!admit()) return HandoffStatus.BUSY
                onText(text); HandoffStatus.COPIED
            }
            else -> HandoffStatus.INVALID
        }
    }

    private fun admit(): Boolean = synchronized(lock) {
        val t = now()
        while (recent.isNotEmpty() && t - recent.first() >= 60_000) recent.removeFirst()
        if (recent.size >= MAX_PER_MINUTE) false else { recent.addLast(t); true }
    }

    suspend fun sendUrl(url: String, title: String?, send: suspend (BridgeMessage) -> Boolean): String {
        val link = HandoffRules.checkUrl(url, title) ?: return done("url", HandoffStatus.INVALID)
        return send(HandoffPayload(UUID.randomUUID().toString(), HandoffKind.URL, url = link.url, title = link.title), send)
    }

    suspend fun sendText(text: String, send: suspend (BridgeMessage) -> Boolean): String {
        val t = HandoffRules.checkText(text) ?: return done("text", HandoffStatus.INVALID)
        return send(HandoffPayload(UUID.randomUUID().toString(), HandoffKind.TEXT, text = t), send)
    }

    /** What another app shared as text: a lone link goes as a link (with the subject as title), anything else as text. */
    suspend fun sendShared(text: String?, subject: String?, send: suspend (BridgeMessage) -> Boolean): String {
        val trimmed = text?.trim().orEmpty()
        return if (HandoffRules.checkUrl(trimmed) != null) sendUrl(trimmed, subject, send) else sendText(text.orEmpty(), send)
    }

    private fun done(kind: String, status: String): String { onCompleted?.invoke("out", kind, status); return status }

    private suspend fun send(p: HandoffPayload, send: suspend (BridgeMessage) -> Boolean): String {
        val answer = CompletableDeferred<String>()
        pending[p.id] = answer
        try {
            if (!send(BridgeMessage.create(MessageType.HANDOFF_OPEN, p))) return done(p.kind, HandoffStatus.UNAVAILABLE)
            return done(p.kind, withTimeoutOrNull(ACK_TIMEOUT_MS) { answer.await() } ?: HandoffStatus.UNAVAILABLE)
        } finally {
            pending.remove(p.id)
        }
    }
}
