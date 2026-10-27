package com.bridge.app.service

import android.content.Context
import com.bridge.app.network.PairedDevice
import com.bridge.app.network.PairedDeviceStore
import com.bridge.app.protocol.BridgeJson
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.builtins.ListSerializer

/**
 * Persists the paired PC(s) — id, name, public key, last known address — so
 * the connection service can reconnect after the app or phone restarts. (The
 * in-memory placeholder lost the pairing as soon as the activity died.) Room
 * remains the production target per blueprint section 5/14.
 */
class SharedPrefsPairedDeviceStore(context: Context) : PairedDeviceStore {
    private val prefs = context.applicationContext.getSharedPreferences("bridge_pairing", Context.MODE_PRIVATE)
    private val serializer = ListSerializer(PairedDevice.serializer())

    override suspend fun save(device: PairedDevice) = withContext(Dispatchers.IO) {
        synchronized(LOCK) { write(read().filter { it.deviceId != device.deviceId } + device) }
    }

    override suspend fun list(): List<PairedDevice> = withContext(Dispatchers.IO) { synchronized(LOCK) { read() } }

    override suspend fun revoke(deviceId: String) = withContext(Dispatchers.IO) {
        synchronized(LOCK) { write(read().filter { it.deviceId != deviceId }) }
    }

    /** Any paired PC (the most recently paired one); see PcChooser for which one to connect to. */
    suspend fun current(): PairedDevice? = list().lastOrNull()

    private fun read(): List<PairedDevice> {
        val json = prefs.getString(KEY, null) ?: return emptyList()
        return runCatching { BridgeJson.decodeFromString(serializer, json) }.getOrDefault(emptyList())
    }

    private fun write(devices: List<PairedDevice>) {
        prefs.edit().putString(KEY, BridgeJson.encodeToString(serializer, devices)).commit()
    }

    private companion object {
        const val KEY = "paired_devices"
        val LOCK = Any()
    }
}
