package com.bridge.app.service

import android.content.Context
import com.bridge.app.network.RelayConfig
import com.bridge.app.network.RelayConfigStore

/**
 * Each PC's relay (from relay.config), in app-private SharedPreferences. The relay key only lets a
 * device use the relay; it can't open Bridge's end-to-end encryption.
 */
class SharedPrefsRelayConfigStore(context: Context) : RelayConfigStore {
    private val prefs = context.applicationContext.getSharedPreferences("bridge_relay", Context.MODE_PRIVATE)

    override fun get(pcId: String): RelayConfig? {
        val host = prefs.getString("$pcId.host", null) ?: return null
        val port = prefs.getInt("$pcId.port", 0)
        val key = RelayConfig.decodeKey(prefs.getString("$pcId.key", null)) ?: return null
        return RelayConfig(host, port, key)
    }

    override fun put(pcId: String, config: RelayConfig?) {
        prefs.edit().apply {
            if (config == null) { remove("$pcId.host"); remove("$pcId.port"); remove("$pcId.key") }
            else { putString("$pcId.host", config.host); putInt("$pcId.port", config.port); putString("$pcId.key", RelayConfig.encodeKey(config.key)) }
        }.apply()
    }
}
