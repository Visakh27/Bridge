package com.bridge.app.core

import android.app.Application
import android.app.NotificationChannel
import android.app.NotificationManager
import android.os.Build
import com.bridge.app.notifications.ListenerNotificationSource
import com.bridge.app.notifications.NotificationFilter
import com.bridge.app.notifications.NotificationMirror
import com.bridge.app.notifications.SharedPrefsNotificationFilterStore
import com.bridge.app.security.DeviceIdentity

class BridgeApplication : Application() {
    lateinit var identity: DeviceIdentity
        private set

    /** Mirroring rules, per-app choices and the PC's notification requests (shared by listener and service). */
    val notificationMirror: NotificationMirror by lazy {
        NotificationMirror(NotificationFilter(SharedPrefsNotificationFilterStore(this)), ListenerNotificationSource, packageName)
    }

    companion object {
        const val CONNECTION_CHANNEL_ID = "bridge_connection"
    }

    override fun onCreate() {
        super.onCreate()
        identity = DeviceIdentity.loadOrCreate(this)
        createNotificationChannels()
    }

    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CONNECTION_CHANNEL_ID, "Bridge connection", NotificationManager.IMPORTANCE_LOW)
        )
    }
}
