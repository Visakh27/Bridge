package com.bridge.app.service

import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import com.bridge.app.network.ClipboardAccess

/** Real Android ClipboardManager-backed implementation of ClipboardAccess. */
class AndroidClipboardAccess(private val context: Context) : ClipboardAccess {
    private val manager = context.getSystemService(Context.CLIPBOARD_SERVICE) as ClipboardManager
    private var listener: ((String) -> Unit)? = null

    init {
        manager.addPrimaryClipChangedListener {
            readText()?.let { text -> listener?.invoke(text) }
        }
    }

    override fun readText(): String? {
        val clip = manager.primaryClip ?: return null
        if (clip.itemCount == 0) return null
        return clip.getItemAt(0).coerceToText(context)?.toString()
    }

    override fun writeText(text: String) {
        manager.setPrimaryClip(ClipData.newPlainText("bridge", text))
    }

    override fun setChangeListener(listener: (String) -> Unit) {
        this.listener = listener
    }
}
