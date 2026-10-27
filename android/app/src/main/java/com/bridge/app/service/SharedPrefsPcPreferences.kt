package com.bridge.app.service

import android.content.Context
import com.bridge.app.network.PcPreferences

/** [PcPreferences] in SharedPreferences, next to the pairing store. */
class SharedPrefsPcPreferences(context: Context) : PcPreferences {
    private val prefs = context.applicationContext.getSharedPreferences("bridge_pairing", Context.MODE_PRIVATE)

    override var pinnedPcId: String?
        get() = prefs.getString(PINNED, null)
        set(value) { prefs.edit().apply { if (value == null) remove(PINNED) else putString(PINNED, value) }.apply() }

    override var lastConnectedPcId: String?
        get() = prefs.getString(LAST, null)
        set(value) { prefs.edit().apply { if (value == null) remove(LAST) else putString(LAST, value) }.apply() }

    private companion object {
        const val PINNED = "pinned_pc"
        const val LAST = "last_connected_pc"
    }
}
