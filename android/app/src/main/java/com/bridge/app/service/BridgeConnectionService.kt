package com.bridge.app.service

import android.app.Notification
import android.app.NotificationManager
import android.app.Service
import android.content.Intent
import android.os.BatteryManager
import android.os.IBinder
import androidx.core.app.NotificationCompat
import com.bridge.app.R
import com.bridge.app.core.BridgeApplication
import com.bridge.app.network.*
import com.bridge.app.protocol.*
import kotlinx.coroutines.*
import java.io.File

/**
 * Section 18 (Android background operation): keeps the encrypted session
 * alive as a foreground service, handles reconnect with backoff, and routes
 * incoming/outgoing messages to FileTransferClient / ClipboardSyncClient.
 * This is the Android counterpart to Bridge.Core.BridgeSession on Windows.
 */
class BridgeConnectionService : Service() {

    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.IO)
    private var channel: EncryptedChannel? = null
    private lateinit var fileTransfer: FileTransferClient
    private lateinit var clipboardSync: ClipboardSyncClient

    companion object {
        var instance: BridgeConnectionService? = null
        private val backoffSteps = listOf(1_000L, 2_000L, 5_000L, 10_000L, 30_000L)
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
        fileTransfer = FileTransferClient { /* progress -> broadcast to UI via a StateFlow in a full implementation */ }
        clipboardSync = ClipboardSyncClient(AndroidClipboardAccess(this), scope)
        startForeground(1, buildNotification("Bridge is connected"))
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        val host = intent?.getStringExtra("host")
        val port = intent?.getIntExtra("port", -1) ?: -1
        if (host != null && port > 0) scope.launch { connectAndRun(host, port) }
        return START_STICKY
    }

    private suspend fun connectAndRun(host: String, port: Int) {
        var attempt = 0
        while (isActive) {
            try {
                val identity = (application as BridgeApplication).identity
                val transport = TcpTransport.connect(host, port)
                val ch = EncryptedChannel.handshakeAsInitiator(transport, identity.deviceId)
                channel = ch
                clipboardSync.channel = ch
                receiveLoop(ch)
                attempt = 0 // clean disconnect resets backoff on next call
            } catch (e: Exception) {
                val delayMs = backoffSteps[minOf(attempt, backoffSteps.size - 1)]
                attempt++
                delay(delayMs)
            }
        }
    }

    private suspend fun receiveLoop(channel: EncryptedChannel) {
        val downloadDir = File(getExternalFilesDir(null), "Bridge")
        while (channel.isConnected) {
            val message = channel.receive()
            when (message.type) {
                MessageType.CLIPBOARD_UPDATE -> clipboardSync.handleIncoming(message)
                MessageType.FILE_START, MessageType.FILE_CHUNK, MessageType.FILE_END, MessageType.FILE_CANCEL ->
                    fileTransfer.handleIncoming(channel, message, downloadDir)
                MessageType.PRESENCE_PING -> channel.send(BridgeMessage.create(MessageType.PRESENCE_PONG, Unit))
                MessageType.PRESENCE_PONG -> { /* reset watchdog timer in a full implementation */ }
            }
        }
    }

    /** Called periodically (WorkManager or a coroutine loop) to report battery/status upstream. */
    fun reportDeviceStatus() {
        val batteryManager = getSystemService(BATTERY_SERVICE) as BatteryManager
        val level = batteryManager.getIntProperty(BatteryManager.BATTERY_PROPERTY_CAPACITY)
        val charging = batteryManager.isCharging
        val ch = channel ?: return
        scope.launch {
            ch.send(BridgeMessage.create(MessageType.DEVICE_STATUS, DeviceStatusPayload(level, charging, "good")))
        }
    }

    fun sendMessage(message: BridgeMessage) {
        val ch = channel ?: return
        scope.launch { ch.send(message) }
    }

    fun sendFile(file: File) {
        val ch = channel ?: return
        scope.launch { fileTransfer.sendFile(ch, file) }
    }

    private fun buildNotification(text: String): Notification =
        NotificationCompat.Builder(this, "bridge_connection")
            .setContentTitle(getString(R.string.connected_service_title))
            .setContentText(text)
            .setSmallIcon(android.R.drawable.stat_sys_data_bluetooth) // replace with a real Bridge icon
            .setOngoing(true)
            .build()

    override fun onDestroy() {
        instance = null
        scope.cancel()
        channel?.close()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null
}
