package com.bridge.app.service

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.os.Handler
import android.os.Looper
import com.bridge.app.network.ClipboardAccess

/**
 * Real Android ClipboardManager-backed implementation of ClipboardAccess.
 * Must be constructed on the main thread; writes from the session's IO
 * thread are posted back to it.
 *
 * Platform limit: since Android 10 only the foreground app (or the default
 * IME) may read the clipboard, so phone→PC sync fires while Bridge is open;
 * PC→phone writes work in the background.
 */
class AndroidClipboardAccess(private val context: Context) : ClipboardAccess {
    private val manager = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    private val main = Handler(Looper.getMainLooper())
    @Volatile private var listener: ((String) -> Unit)? = null

    init {
        manager.addPrimaryClipChangedListener {
            runCatching { readText() }.getOrNull()?.let { text -> listener?.invoke(text) }
        }
    }

    override fun readText(): String? {
        val clip = manager.primaryClip ?: return null
        if (clip.itemCount == 0) return null
        return clip.getItemAt(0).coerceToText(context)?.toString()
    }

    override fun writeText(text: String) {
        main.post { runCatching { manager.setPrimaryClip(ClipData.newPlainText("Bridge", text)) } }
    }

    override fun setChangeListener(listener: (String) -> Unit) {
        this.listener = listener
    }
}
