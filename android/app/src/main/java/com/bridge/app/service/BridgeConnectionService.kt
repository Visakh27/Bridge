package com.bridge.app.service

import android.app.Notification
import android.app.NotificationManager
import android.app.Service
import android.content.Context
import android.content.Intent
import android.content.pm.ServiceInfo
import android.os.BatteryManager
import android.os.Build
import android.os.IBinder
import androidx.core.app.NotificationCompat
import androidx.core.app.ServiceCompat
import androidx.core.content.ContextCompat
import com.bridge.app.R
import com.bridge.app.core.BridgeApplication
import com.bridge.app.network.*
import com.bridge.app.notifications.NotificationMirror
import com.bridge.app.notifications.PhoneNotification
import com.bridge.app.protocol.*
import java.io.File
import kotlinx.coroutines.*
import kotlinx.coroutines.channels.BufferOverflow
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * Section 18 (Android background operation): keeps the encrypted session to
 * the paired PC alive as a foreground service. [ReconnectLoop] reconnects with
 * backoff, re-finds a PC whose address changed via local discovery, and
 * reacts to network changes immediately.
 * The protocol work happens in the platform-agnostic [BridgeClientSession];
 * this class supplies Android pieces (battery, clipboard, notification,
 * persisted pairing) and exposes [state] for the UI.
 */
class BridgeConnectionService : Service() {

    enum class Status { DISCONNECTED, CONNECTING, CONNECTED, UNPAIRED }

    data class State(
        val status: Status = Status.DISCONNECTED,
        val pcName: String? = null,
        val lastTransfer: String? = null,
    )

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var connectJob: Job? = null
    @Volatile private var session: BridgeClientSession? = null
    @Volatile private var reconnectLoop: ReconnectLoop? = null
    private lateinit var network: LocalNetworkMonitor
    private lateinit var store: SharedPrefsPairedDeviceStore
    private lateinit var fileTransfer: FileTransferClient
    private lateinit var clipboardSync: ClipboardSyncClient
    /** Notification posts/dismissals go out one at a time, in order (a dismiss never overtakes its post). */
    private val notificationQueue = Channel<Pair<BridgeClientSession, BridgeMessage>>(256, BufferOverflow.DROP_OLDEST)

    companion object {
        private const val NOTIFICATION_ID = 1
        private const val PREFS = "bridge_settings"
        private const val KEY_CLIPBOARD = "clipboard_sync"

        @Volatile var instance: BridgeConnectionService? = null
            private set

        private val _state = MutableStateFlow(State())
        val state: StateFlow<State> = _state.asStateFlow()

        fun start(context: Context) {
            ContextCompat.startForegroundService(context, Intent(context, BridgeConnectionService::class.java))
        }

        fun isClipboardSyncEnabled(context: Context): Boolean =
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).getBoolean(KEY_CLIPBOARD, false)

        /** Clipboard sync is opt-in and remembered across restarts. */
        fun setClipboardSyncEnabled(context: Context, enabled: Boolean) {
            context.getSharedPreferences(PREFS, Context.MODE_PRIVATE).edit().putBoolean(KEY_CLIPBOARD, enabled).apply()
            instance?.clipboardSync?.enabled = enabled
        }

        /** A notification was posted or updated on the phone (from BridgeNotificationListener). */
        fun onPhoneNotificationPosted(mirror: NotificationMirror, n: PhoneNotification) {
            val service = instance
            val s = service?.session
            if (s == null || !s.peerSupports(Features.NOTIFICATIONS)) {
                mirror.filter.noteSeen(n.appId, n.appName) // still listed in the per-app settings
                return
            }
            val message = mirror.update(n, s.peerSupports(Features.NOTIFICATIONS_ACTIONS)) ?: return
            service.queueNotificationMessage(s, message)
        }

        fun onPhoneNotificationRemoved(mirror: NotificationMirror, n: PhoneNotification) {
            val payload = mirror.onRemoved(n) ?: return
            val service = instance ?: return
            val s = service.session ?: return
            service.queueNotificationMessage(s, BridgeMessage.create(MessageType.NOTIFICATION_DISMISS, payload))
        }

        /** Per-app setting changed on the phone; turning an app off also clears its notifications on the PC. */
        fun setAppMirrored(mirror: NotificationMirror, appId: String, mirrored: Boolean) {
            val dismissals = mirror.setAppAllowed(appId, mirrored)
            val service = instance ?: return
            val s = service.session ?: return
            dismissals.forEach { service.queueNotificationMessage(s, BridgeMessage.create(MessageType.NOTIFICATION_DISMISS, it)) }
        }

        /** Forgets the paired PC on this phone and stops connecting to it. */
        suspend fun forgetPc(context: Context) {
            val store = SharedPrefsPairedDeviceStore(context)
            store.current()?.let { store.revoke(it.deviceId) }
            instance?.stopSelf()
            _state.value = State(Status.DISCONNECTED)
        }
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
        store = SharedPrefsPairedDeviceStore(this)
        network = LocalNetworkMonitor(this)
        // Unfinished sends/receives survive reconnects and app restarts (24 h).
        fileTransfer = FileTransferClient(TransferStateStore(File(filesDir, "bridge-state")), ::onTransferProgress)
        clipboardSync = ClipboardSyncClient(AndroidClipboardAccess(this), scope).apply {
            enabled = isClipboardSyncEnabled(this@BridgeConnectionService)
        }
        scope.launch {
            val mirror = (application as BridgeApplication).notificationMirror
            for ((s, message) in notificationQueue) {
                // A post that didn't make it is sent again on the next update instead of being skipped as a repeat.
                if (!s.send(message) && message.type == MessageType.NOTIFICATION_POST)
                    mirror.forget(message.payloadAs<NotificationPostPayload>().key)
            }
        }
        // connectedDevice FGS type; on Android 14 it also needs one of the
        // network permissions declared in the manifest (CHANGE_NETWORK_STATE).
        ServiceCompat.startForeground(
            this, NOTIFICATION_ID, buildNotification(getString(R.string.connecting_service_text)),
            if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) ServiceInfo.FOREGROUND_SERVICE_TYPE_CONNECTED_DEVICE else 0,
        )
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        // START_STICKY restarts arrive with a null intent: the pairing comes from
        // the persisted store, so that still reconnects. Only one loop at a time.
        if (connectJob?.isActive != true) connectJob = scope.launch { connectLoop() }
        return START_STICKY
    }

    private suspend fun connectLoop() {
        val app = application as BridgeApplication
        val identity = app.identity
        val loop = ReconnectLoop(
            store = store,
            discovery = PcDiscovery(targets = network::broadcastTargets),
            sessionFactory = { pc ->
                BridgeClientSession(
                    identity, pc, fileTransfer, clipboardSync, downloadDir(),
                    listener = object : BridgeClientSession.Listener {
                        override fun onConnected(peerFeatures: List<String>) {
                            _state.update { it.copy(status = Status.CONNECTED, pcName = pc.deviceName) }
                            updateNotification(getString(R.string.connected_service_text, pc.deviceName))
                        }
                        override fun onDisconnected() {
                            _state.update { it.copy(status = Status.CONNECTING) }
                            updateNotification(getString(R.string.connecting_service_text))
                        }
                    },
                    statusProvider = ::currentStatus,
                    notifications = app.notificationMirror,
                ).also { session = it }
            },
            listener = object : ReconnectLoop.Listener {
                override fun onConnecting(pc: PairedDevice) {
                    _state.update { it.copy(status = Status.CONNECTING, pcName = pc.deviceName) }
                    updateNotification(getString(R.string.connecting_service_text))
                }
                override fun onAddressChanged(pc: PairedDevice) {
                    // Found again via local discovery (new IP or port); already saved.
                }
                override fun onUnpaired(pc: PairedDevice) {
                    // The PC forgot this phone: stop retrying and ask the user to pair again.
                    _state.value = State(Status.UNPAIRED, pc.deviceName)
                }
            },
        )
        reconnectLoop = loop
        network.start(onAvailable = loop::wake, onLost = loop::dropCurrentSession)
        try {
            val outcome = loop.run()
            if (outcome == ReconnectLoop.Outcome.NOT_PAIRED) _state.value = State(Status.DISCONNECTED)
        } finally {
            network.stop()
            reconnectLoop = null
            session = null
        }
        stopSelf()
    }

    private fun currentStatus(): DeviceStatusPayload {
        val batteryManager = getSystemService(BATTERY_SERVICE) as BatteryManager
        val level = batteryManager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
        return DeviceStatusPayload(if (level in 0..100) level else -1, batteryManager.isCharging, "good")
    }

    private fun downloadDir(): File = File(getExternalFilesDir(null) ?: filesDir, "Bridge")

    private fun onTransferProgress(progress: TransferProgress) {
        val sending = progress.direction == TransferDirection.SENDING
        val verb = if (sending) "Sending" else "Receiving"
        // Folder transfers: "Holiday — file 12 of 340: IMG_0012.jpg"
        val count = progress.batchCount
        val index = progress.batchIndex
        val name = if (count != null && index != null)
            "${progress.relativePath?.substringBefore('/')} — file ${index + 1} of $count: ${progress.fileName}" else progress.fileName
        val percent = (progress.fraction * 100).toInt()
        val text = when (progress.status) {
            TransferStatus.IN_PROGRESS -> if (progress.resumedFrom > 0) "Resuming $name ($percent%)" else "$verb $name ($percent%)"
            TransferStatus.COMPLETED -> if (sending) "Sent $name" else "Received ${progress.savedFile?.name ?: name}"
            TransferStatus.INTERRUPTED -> "$name paused at $percent% — it will continue when the PC is back"
            TransferStatus.FAILED -> "$name didn't transfer correctly"
            TransferStatus.CANCELLED -> "$name was cancelled"
        }
        _state.update { it.copy(lastTransfer = text) }
    }

    private fun queueNotificationMessage(s: BridgeClientSession, message: BridgeMessage) {
        notificationQueue.trySend(s to message)
    }

    /** Sends a file to the PC; [deleteAfter] removes a temporary copy afterwards. Returns false if not connected. */
    /**
     * Sends a file to the PC. With [deleteAfter] the file is a private copy that is
     * removed once the send is finished (kept while an interrupted send waits to
     * resume). Returns false if not connected.
     */
    fun sendFile(file: File, displayName: String = file.name, deleteAfter: Boolean = false): Boolean {
        val s = session
        if (s == null || !s.isConnected) {
            if (deleteAfter) file.delete()
            return false
        }
        scope.launch { s.sendFile(file, displayName, deleteWhenDone = deleteAfter) }
        return true
    }

    /**
     * Sends a folder picked with the system folder picker. Android's storage
     * framework only allows reading it through content URIs, so each file is
     * copied to private cache first (needs free space about the folder's size);
     * copies are deleted as each file is confirmed. Returns false if not connected
     * or there isn't room.
     */
    fun sendFolder(treeUri: android.net.Uri, onResult: (String) -> Unit) {
        val s = session
        if (s == null || !s.isConnected) { onResult("Bridge isn't connected to your PC right now."); return }
        scope.launch {
            val items = try {
                FolderCopier(this@BridgeConnectionService).copy(treeUri)
            } catch (e: Exception) {
                onResult(e.message ?: "Couldn't read that folder.")
                return@launch
            }
            if (items.isEmpty()) { onResult("That folder has no files to send."); return@launch }
            onResult("Sending ${items.size} files to your PC…")
            val results = s.sendFolder(items)
            if (results == null) items.forEach { it.file.delete() }
        }
    }

    private fun buildNotification(text: String): Notification =
        NotificationCompat.Builder(this, BridgeApplication.CONNECTION_CHANNEL_ID)
            .setContentTitle(getString(R.string.app_name))
            .setContentText(text)
            .setSmallIcon(R.drawable.ic_stat_bridge)
            .setOngoing(true)
            .build()

    private fun updateNotification(text: String) {
        getSystemService(NotificationManager::class.java)?.notify(NOTIFICATION_ID, buildNotification(text))
    }

    override fun onDestroy() {
        instance = null
        network.stop()
        fileTransfer.state.close() // write any pending resume state
        notificationQueue.close()
        scope.cancel()
        session?.close()
        if (_state.value.status != Status.UNPAIRED) _state.update { it.copy(status = Status.DISCONNECTED) }
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
