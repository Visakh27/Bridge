package com.bridge.app.network

/** Where the PC choices are kept (SharedPreferences on a phone, memory in tests). */
interface PcPreferences {
    /** The PC the user chose in Settings, or null to use whichever was connected last. */
    var pinnedPcId: String?
    /** The PC this phone was connected to most recently. */
    var lastConnectedPcId: String?
}

class InMemoryPcPreferences(override var pinnedPcId: String? = null, override var lastConnectedPcId: String? = null) : PcPreferences

/**
 * Which paired PC to connect to when there are several. The phone keeps one
 * connection at a time: it tries the PC the user chose (if any), then the one
 * it was connected to last, then the rest, newest pairing first. Plain Kotlin,
 * so it runs in JVM tests.
 */
class PcChooser(private val prefs: PcPreferences) {
    private val lock = Any()

    /** [paired] (as stored: oldest pairing first) in the order to try them. */
    fun order(paired: List<PairedDevice>): List<PairedDevice> = synchronized(lock) {
        val byNewest = paired.asReversed().distinctBy { it.deviceId }
        val first = listOfNotNull(prefs.pinnedPcId, prefs.lastConnectedPcId).distinct()
        first.mapNotNull { id -> byNewest.firstOrNull { it.deviceId == id } } + byNewest.filter { it.deviceId !in first }
    }

    /** The PC to show as "this phone's PC" when not connected. */
    fun preferred(paired: List<PairedDevice>): PairedDevice? = order(paired).firstOrNull()

    val pinnedPcId: String? get() = synchronized(lock) { prefs.pinnedPcId }

    /** A session with [deviceId] started. */
    fun connected(deviceId: String) = synchronized(lock) { prefs.lastConnectedPcId = deviceId }

    /** The user chose a PC (null: go back to "whichever was connected last"). */
    fun pin(deviceId: String?) = synchronized(lock) { prefs.pinnedPcId = deviceId }

    /** A PC was forgotten (by the user, or it forgot this phone). */
    fun forgotten(deviceId: String) = synchronized(lock) {
        if (prefs.pinnedPcId == deviceId) prefs.pinnedPcId = null
        if (prefs.lastConnectedPcId == deviceId) prefs.lastConnectedPcId = null
    }
}
