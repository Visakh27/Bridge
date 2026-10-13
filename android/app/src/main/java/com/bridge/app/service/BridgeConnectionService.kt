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
import com.bridge.app.protocol.*
import java.io.File
import kotlinx.coroutines.*
import kotlinx.coroutines.flow.MutableStateFlow
import kotlinx.coroutines.flow.StateFlow
import kotlinx.coroutines.flow.asStateFlow
import kotlinx.coroutines.flow.update

/**
 * Section 18 (Android background operation): keeps the encrypted session to
 * the paired PC alive as a foreground service and reconnects with backoff.
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
    private lateinit var store: SharedPrefsPairedDeviceStore
    private lateinit var fileTransfer: FileTransferClient
    private lateinit var clipboardSync: ClipboardSyncClient

    companion object {
        private const val NOTIFICATION_ID = 1
        private const val PREFS = "bridge_settings"
        private const val KEY_CLIPBOARD = "clipboard_sync"
        private val backoffSteps = listOf(1_000L, 2_000L, 5_000L, 10_000L, 30_000L)

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
        fileTransfer = FileTransferClient(::onTransferProgress)
        clipboardSync = ClipboardSyncClient(AndroidClipboardAccess(this), scope).apply {
            enabled = isClipboardSyncEnabled(this@BridgeConnectionService)
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
        var attempt = 0
        while (currentCoroutineContext().isActive) {
            val pc = store.current()
            if (pc == null) {
                _state.value = State(Status.DISCONNECTED)
                stopSelf()
                return
            }
            _state.update { it.copy(status = Status.CONNECTING, pcName = pc.deviceName) }
            updateNotification(getString(R.string.connecting_service_text))

            val identity = (application as BridgeApplication).identity
            val s = BridgeClientSession(
                identity, pc, fileTransfer, clipboardSync, downloadDir(),
                listener = object : BridgeClientSession.Listener {
                    override fun onConnected(peerFeatures: List<String>) {
                        _state.update { it.copy(status = Status.CONNECTED, pcName = pc.deviceName) }
                        updateNotification(getString(R.string.connected_service_text, pc.deviceName))
                    }
                    override fun onDisconnected() {
                        _state.update { it.copy(status = Status.CONNECTING) }
                    }
                },
                statusProvider = ::currentStatus,
            )
            session = s
            val startedAt = System.currentTimeMillis()
            try {
                s.run()
            } catch (e: CancellationException) {
                throw e
            } catch (e: DeviceUnpairedException) {
                // The PC forgot this phone: stop retrying and ask the user to pair again.
                store.revoke(pc.deviceId)
                _state.value = State(Status.UNPAIRED, pc.deviceName)
                session = null
                stopSelf()
                return
            } catch (e: Exception) {
                // Unreachable, timed out, or the PC failed authentication: back off and retry.
            } finally {
                session = null
            }

            if (System.currentTimeMillis() - startedAt > 30_000) attempt = 0 // it was a real session
            delay(backoffSteps[minOf(attempt, backoffSteps.size - 1)])
            attempt++
        }
    }

    private fun currentStatus(): DeviceStatusPayload {
        val batteryManager = getSystemService(BATTERY_SERVICE) as BatteryManager
        val level = batteryManager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
        return DeviceStatusPayload(if (level in 0..100) level else -1, batteryManager.isCharging, "good")
    }

    private fun downloadDir(): File = File(getExternalFilesDir(null) ?: filesDir, "Bridge")

    private fun onTransferProgress(progress: TransferProgress) {
        val verb = if (progress.direction == TransferDirection.SENDING) "Sending" else "Receiving"
        val text = when (progress.status) {
            TransferStatus.IN_PROGRESS -> "$verb ${progress.fileName} (${(progress.fraction * 100).toInt()}%)"
            TransferStatus.COMPLETED ->
                if (progress.direction == TransferDirection.SENDING) "Sent ${progress.fileName}"
                else "Received ${progress.savedFile?.name ?: progress.fileName}"
            TransferStatus.FAILED -> "${progress.fileName} didn't transfer correctly"
            TransferStatus.CANCELLED -> "${progress.fileName} was cancelled"
        }
        _state.update { it.copy(lastTransfer = text) }
    }

    fun sendMessage(message: BridgeMessage) {
        val s = session ?: return
        scope.launch { s.send(message) }
    }

    /** Sends a file to the PC; [deleteAfter] removes a temporary copy afterwards. Returns false if not connected. */
    fun sendFile(file: File, displayName: String = file.name, deleteAfter: Boolean = false): Boolean {
        val s = session
        if (s == null || !s.isConnected) {
            if (deleteAfter) file.delete()
            return false
        }
        scope.launch {
            try { s.sendFile(file, displayName) } finally { if (deleteAfter) file.delete() }
        }
        return true
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
        scope.cancel()
        session?.close()
        if (_state.value.status != Status.UNPAIRED) _state.update { it.copy(status = Status.DISCONNECTED) }
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
