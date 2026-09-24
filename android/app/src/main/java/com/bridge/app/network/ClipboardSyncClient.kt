package com.bridge.app.network

import com.bridge.app.protocol.*
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
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
    var enabled: Boolean = false
    var channel: EncryptedChannel? = null
    private var suppressNextLocalEvent = false

    init {
        clipboard.setChangeListener { newText -> onLocalClipboardChanged(newText) }
    }

    private fun onLocalClipboardChanged(newText: String) {
        if (!enabled) return
        val ch = channel ?: return
        if (suppressNextLocalEvent) { suppressNextLocalEvent = false; return }
        if (looksSensitive(newText)) return

        scope.launch(Dispatchers.IO) {
            ch.send(BridgeMessage.create(MessageType.CLIPBOARD_UPDATE, ClipboardUpdatePayload("text", newText)))
        }
    }

    suspend fun handleIncoming(message: BridgeMessage) {
        if (!enabled) return
        val payload = message.payloadAs<ClipboardUpdatePayload>()
        if (payload.format != "text") return
        suppressNextLocalEvent = true
        clipboard.writeText(payload.data)
    }

    private fun looksSensitive(text: String): Boolean {
        if (text.length > 10_000) return true
        val lower = text.lowercase()
        return lower.contains("-----begin") || lower.contains("password:") || lower.contains("secret_key")
    }
}
