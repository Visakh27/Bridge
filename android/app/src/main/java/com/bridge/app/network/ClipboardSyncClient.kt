package com.bridge.app.network

import com.bridge.app.protocol.*
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.launch

/**
 * Platform-agnostic clipboard sync coordinator, mirrored from
 * Bridge.Network.ClipboardSyncService on Windows. Actual ClipboardManager
 * access is injected so this class stays testable without an Android runtime.
 * `scope` should be the caller's lifecycle/service scope (not GlobalScope).
 */
interface ClipboardAccess {
    fun readText(): String?
    fun writeText(text: String)
    fun setChangeListener(listener: (String) -> Unit)
}

class ClipboardSyncClient(private val clipboard: ClipboardAccess, private val scope: CoroutineScope) {
    companion object { const val MAX_TEXT_LENGTH = 10_000 }

    /** Opt-in (docs/security.md); off by default. */
    @Volatile var enabled: Boolean = false
    /** Whether the PC advertised the "clipboard" feature. */
    @Volatile var peerSupportsClipboard: Boolean = false
    @Volatile var channel: EncryptedChannel? = null

    private val lock = Any()
    private var lastSynced: String? = null // last text sent or received, to break echo loops

    init {
        clipboard.setChangeListener { newText -> onLocalClipboardChanged(newText) }
    }

    private fun onLocalClipboardChanged(newText: String) {
        val ch = channel ?: return
        if (!enabled || !peerSupportsClipboard || looksSensitive(newText)) return
        synchronized(lock) {
            if (newText == lastSynced) return // our own write echoing back, or no change
            lastSynced = newText
        }
        scope.launch {
            runCatching { ch.send(BridgeMessage.create(MessageType.CLIPBOARD_UPDATE, ClipboardUpdatePayload("text", newText))) }
        }
    }

    fun handleIncoming(message: BridgeMessage) {
        if (!enabled) return
        val payload = message.payloadAs<ClipboardUpdatePayload>()
        if (payload.format != "text" || payload.data.length > MAX_TEXT_LENGTH) return
        synchronized(lock) {
            if (payload.data == lastSynced) return
            lastSynced = payload.data // so the resulting local change event isn't echoed back
        }
        clipboard.writeText(payload.data)
    }

    private fun looksSensitive(text: String): Boolean {
        if (text.length > MAX_TEXT_LENGTH) return true
        val lower = text.lowercase()
        return lower.contains("-----begin") || lower.contains("password:") || lower.contains("secret_key")
    }
}
