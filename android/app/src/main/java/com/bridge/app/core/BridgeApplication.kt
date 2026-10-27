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

    /** Work that outlives a single connection (photo requests). */
    val appScope = kotlinx.coroutines.CoroutineScope(kotlinx.coroutines.SupervisorJob() + kotlinx.coroutines.Dispatchers.IO)

    /** Answers the PC's photo requests once the user turns photos on (Settings). */
    val photoService: com.bridge.app.photos.PhotoService by lazy {
        com.bridge.app.photos.PhotoService(
            com.bridge.app.photos.MediaStorePhotoLibrary(this, java.io.File(cacheDir, "share")), // swept like share copies
            enabled = { BridgePrefs.photosEnabled(this) },
            scope = appScope,
        ).also {
            it.onRequestHandled = { kind, status ->
                // The PC loads dozens of previews at once: log those only when they fail.
                if (!(kind == "thumb" && status == com.bridge.app.protocol.PhotoStatus.OK))
                    diagnostics.info("photos.request", LogField.code("kind", kind), LogField.code("status", status))
            }
        }
    }

    /** Links and text from the PC (and answers to the ones this phone sends). */
    val handoffInbox: com.bridge.app.handoff.HandoffInbox by lazy {
        val notifier = com.bridge.app.handoff.HandoffNotifier(this)
        com.bridge.app.handoff.HandoffInbox(
            enabled = { BridgePrefs.handoffEnabled(this) },
            onLink = notifier::showLink,
            onText = { text -> notifier.copyText(text) { com.bridge.app.service.BridgeConnectionService.writeClipboardFromPc(it) } },
        ).also {
            it.onCompleted = { dir, kind, status ->
                diagnostics.info("handoff", LogField.code("dir", dir), LogField.code("kind", kind), LogField.code("status", status))
            }
        }
    }

    /** Metadata-only diagnostics log (Settings › Diagnostics). */
    val diagnostics: DiagnosticsLog by lazy {
        DiagnosticsLog(java.io.File(filesDir, "logs")).also { it.enabled = BridgePrefs.diagnosticsEnabled(this) }
    }

    /** Mirroring rules, per-app choices and the PC's notification requests (shared by listener and service). */
    val notificationMirror: NotificationMirror by lazy {
        NotificationMirror(NotificationFilter(SharedPrefsNotificationFilterStore(this)), ListenerNotificationSource, packageName).also {
            it.onRequestHandled = { kind, status -> diagnostics.info("notification.action", LogField.code("kind", kind), LogField.code("status", status)) }
        }
    }

    companion object {
        const val CONNECTION_CHANNEL_ID = "bridge_connection"
        const val HANDOFF_CHANNEL_ID = "bridge_handoff"
    }

    override fun onCreate() {
        super.onCreate()
        identity = DeviceIdentity.loadOrCreate(this)
        createNotificationChannels()
        diagnostics.info("app.started", LogField.version("version", BridgePrefs.appVersion(this)), LogField.num("sdk", Build.VERSION.SDK_INT))
    }

    private fun createNotificationChannels() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val manager = getSystemService(NotificationManager::class.java)
        manager.createNotificationChannel(
            NotificationChannel(CONNECTION_CHANNEL_ID, "Bridge connection", NotificationManager.IMPORTANCE_LOW)
        )
        manager.createNotificationChannel(
            NotificationChannel(HANDOFF_CHANNEL_ID, "Links and text from your PC", NotificationManager.IMPORTANCE_DEFAULT)
        )
    }
}
