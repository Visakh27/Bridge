package com.bridge.app.notifications

import android.content.Context
import com.bridge.app.protocol.BridgeJson

/** Keeps the per-app mirroring choices in SharedPreferences (one small JSON value). */
class SharedPrefsNotificationFilterStore(context: Context) : NotificationFilterStore {
    private val prefs = context.applicationContext.getSharedPreferences("bridge_notifications", Context.MODE_PRIVATE)

    override fun load(): NotificationFilter.Saved {
        val json = prefs.getString(KEY, null) ?: return NotificationFilter.Saved()
        return runCatching { BridgeJson.decodeFromString(NotificationFilter.Saved.serializer(), json) }
            .getOrDefault(NotificationFilter.Saved())
    }

    override fun save(saved: NotificationFilter.Saved) {
        prefs.edit().putString(KEY, BridgeJson.encodeToString(NotificationFilter.Saved.serializer(), saved)).apply()
    }

    private companion object { const val KEY = "filter" }
}
