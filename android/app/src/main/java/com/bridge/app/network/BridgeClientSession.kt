package com.bridge.app.network

import com.bridge.app.notifications.NotificationCommandHandler
import com.bridge.app.photos.PhotoRequestHandler
import com.bridge.app.protocol.*
import com.bridge.app.security.IdentityKey
import java.io.File
import java.io.IOException
import java.security.GeneralSecurityException
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import kotlinx.coroutines.withTimeout
import kotlin.coroutines.coroutineContext

/** The PC no longer recognises this phone (it was forgotten there): re-pair. */
class DeviceUnpairedException(message: String) : Exception(message)

/**
 * One connection from the phone to its paired PC — the phone-side counterpart
 * of Bridge.Core.BridgeSession. Platform-agnostic so it can be tested on the
 * JVM; BridgeConnectionService wraps it with reconnect/backoff and Android APIs.
 *
 * Sequence: connect → authenticated handshake (PC must prove the paired key)
 * → capabilities.advertise both ways → message loop + ping/watchdog.
 */
class BridgeClientSession(
    private val identity: IdentityKey,
    private val pc: PairedDevice,
    private val fileTransfer: FileTransferClient,
    private val clipboard: ClipboardSyncClient,
    private val downloadDir: File,
    private val listener: Listener = object : Listener {},
    private val statusProvider: (() -> DeviceStatusPayload?)? = null,
    private val pingIntervalMs: Long = 15_000,
    private val peerTimeoutMs: Long = 45_000,
    private val statusIntervalMs: Long = 60_000,
    private val connect: suspend (host: String, port: Int) -> TcpTransport = TcpTransport::connect,
    /** Carries out the PC's notification requests (reply, buttons, dismiss, mute); null = not offered. */
    private val notifications: NotificationCommandHandler? = null,
    /** Answers the PC's photos.* requests; null = not offered. */
    private val photos: PhotoRequestHandler? = null,
) : SessionHandle {
    interface Listener {
        fun onConnected(peerFeatures: List<String>) {}
        fun onPeerError(error: ErrorPayload) {}
        fun onDisconnected() {}
    }

    companion object {
        val LOCAL_FEATURES = listOf(Features.FILES, Features.CLIPBOARD, Features.NOTIFICATIONS, Features.FILES_RESUME, Features.FILES_FOLDERS)
        private const val SETUP_TIMEOUT_MS = 20_000L
    }

    /** What this phone advertises: notification actions only when something can carry them out. */
    val localFeatures: List<String> = LOCAL_FEATURES +
        listOfNotNull(Features.NOTIFICATIONS_ACTIONS.takeIf { notifications != null }, Features.PHOTOS.takeIf { photos != null })

    @Volatile private var channel: EncryptedChannel? = null
    @Volatile private var lastSeen = System.currentTimeMillis()

    val isConnected: Boolean get() = channel?.isConnected == true

    /**
     * Connects and runs until the connection drops (returns normally) or the
     * calling coroutine is cancelled.
     * @throws DeviceUnpairedException the PC has forgotten this phone.
     * @throws AuthenticationException whoever answered isn't the paired PC.
     * @throws IOException connection failures.
     */
    override suspend fun run() {
        var peerFeatures = emptyList<String>()
        val ch = try { withTimeout(SETUP_TIMEOUT_MS) {
            val transport = connect(pc.host, pc.port)
            val ch = EncryptedChannel.handshakeAsInitiator(transport, identity, pc.publicKey())
            try {
                ch.send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(localFeatures)))
                val reply = ch.receive()
                when (reply.type) {
                    MessageType.CAPABILITIES_ADVERTISE -> {
                        peerFeatures = reply.payloadAs<CapabilitiesPayload>().features
                    }
                    MessageType.ERROR -> {
                        val error = reply.payloadAs<ErrorPayload>()
                        if (error.code == ErrorCodes.DEVICE_UNPAIRED) throw DeviceUnpairedException(error.message)
                        throw AuthenticationException(error.message)
                    }
                    else -> throw IOException("Unexpected first message ${reply.type}")
                }
            } catch (e: Throwable) {
                ch.close()
                throw e
            }
            ch
        } } catch (e: TimeoutCancellationException) {
            // Not a cancellation of our caller: report it as a connection failure.
            throw IOException("Timed out connecting to ${pc.deviceName}", e)
        }

        // Publish the channel *before* announcing the connection, so anything the
        // listener (or the UI it updates) sends straight away isn't dropped.
        channel = ch
        clipboard.peerSupportsClipboard = Features.CLIPBOARD in peerFeatures
        clipboard.channel = ch
        lastSeen = System.currentTimeMillis()
        this.peerFeatures = peerFeatures
        listener.onConnected(peerFeatures)
        try {
            coroutineScope {
                val keepAlive = launch { keepAliveLoop(ch) }
                val status = launch { statusLoop(ch) }
                // Pick up where interrupted transfers left off (and tidy partials nobody came back for).
                val resume = launch {
                    runCatching { FileTransferClient.cleanupExpiredPartials(downloadDir) }
                    try { fileTransfer.resumePending(ch, sendOptions()) } catch (e: IOException) { }
                }
                try {
                    receiveLoop(ch)
                } finally {
                    keepAlive.cancel()
                    status.cancel()
                    resume.cancel()
                }
            }
        } finally {
            channel = null
            if (clipboard.channel === ch) clipboard.channel = null
            ch.close()
            fileTransfer.abortAll(ch)
            listener.onDisconnected()
        }
    }

    private suspend fun receiveLoop(ch: EncryptedChannel) {
        try {
            while (coroutineContext.isActive) {
                val message = ch.receive() // transport/crypto failure ends the session
                lastSeen = System.currentTimeMillis()
                try {
                    dispatch(ch, message)
                } catch (e: CancellationException) {
                    throw e
                } catch (e: IOException) {
                    if (!ch.isConnected) throw e // the connection itself broke
                } catch (e: Exception) {
                    // A bad message or a failing local handler (malformed payload,
                    // clipboard service error, storage problem…) must not drop the
                    // connection; if the connection broke, the next receive fails.
                }
            }
        } catch (e: IOException) {
            // Connection closed or reset.
        } catch (e: GeneralSecurityException) {
            // Frame failed authentication: drop the connection.
        } catch (e: MalformedMessageException) {
            // Undecodable envelope after decryption.
        }
    }

    private suspend fun dispatch(ch: EncryptedChannel, message: BridgeMessage) {
        when (message.type) {
            MessageType.CLIPBOARD_UPDATE -> clipboard.handleIncoming(message)
            MessageType.FILE_START, MessageType.FILE_CHUNK, MessageType.FILE_END,
            MessageType.FILE_CANCEL, MessageType.FILE_ACK, MessageType.FILE_RESUME ->
                fileTransfer.handleIncoming(ch, message, downloadDir)
            MessageType.PRESENCE_PING -> ch.send(BridgeMessage.createEmpty(MessageType.PRESENCE_PONG))
            MessageType.PRESENCE_PONG -> Unit // lastSeen already updated
            MessageType.NOTIFICATION_ACTION, MessageType.NOTIFICATION_DISMISS, MessageType.NOTIFICATION_MUTE ->
                notifications?.handleIncoming(message) { ch.send(it) }
            MessageType.PHOTOS_LIST, MessageType.PHOTOS_THUMB, MessageType.PHOTOS_FETCH ->
                photos?.handleIncoming(message, { ch.send(it) }) { file, name, transferId ->
                    // Resumable like any send; the private copy is deleted once it's done.
                    fileTransfer.sendFile(ch, file, name, sendOptions(), deleteWhenDone = true, transferId = transferId)
                }
            MessageType.CAPABILITIES_ADVERTISE -> {
                val features = message.payloadAs<CapabilitiesPayload>().features
                peerFeatures = features
                clipboard.peerSupportsClipboard = Features.CLIPBOARD in features
            }
            MessageType.ERROR -> listener.onPeerError(message.payloadAs<ErrorPayload>())
            else -> Unit // unknown types from a newer peer are ignored (protocol.md, Versioning)
        }
    }

    private suspend fun keepAliveLoop(ch: EncryptedChannel) {
        try {
            while (coroutineContext.isActive) {
                delay(pingIntervalMs)
                if (System.currentTimeMillis() - lastSeen > peerTimeoutMs) {
                    ch.close() // half-open TCP (PC asleep / Wi-Fi gone): unblock the receive loop
                    return
                }
                ch.send(BridgeMessage.createEmpty(MessageType.PRESENCE_PING))
            }
        } catch (e: IOException) {
            ch.close()
        }
    }

    private suspend fun statusLoop(ch: EncryptedChannel) {
        val provider = statusProvider ?: return
        try {
            while (coroutineContext.isActive) {
                provider()?.let { ch.send(BridgeMessage.create(MessageType.DEVICE_STATUS, it)) }
                delay(statusIntervalMs)
            }
        } catch (e: IOException) {
            // connection gone; receive loop will notice
        }
    }

    /** Sends a message if connected; returns false otherwise. */
    suspend fun send(message: BridgeMessage): Boolean {
        val ch = channel ?: return false
        return try { ch.send(message); true } catch (e: IOException) { false }
    }

    @Volatile private var peerFeatures: List<String> = emptyList()

    /** Whether the connected PC advertised [feature] (false when not connected). */
    fun peerSupports(feature: String): Boolean = isConnected && feature in peerFeatures

    /** What the connected PC supports, for outgoing transfers. */
    fun sendOptions() = SendOptions(pc.deviceId, Features.FILES_RESUME in peerFeatures, Features.FILES_FOLDERS in peerFeatures)

    /**
     * Sends a file if connected; null when not connected. With [deleteWhenDone]
     * the file is a private copy removed once the send is finished (kept while
     * an interrupted send waits to resume).
     */
    suspend fun sendFile(file: File, displayName: String = file.name, deleteWhenDone: Boolean = false): TransferProgress? {
        val ch = channel ?: return null
        return fileTransfer.sendFile(ch, file, displayName, sendOptions(), deleteWhenDone)
    }

    /** Sends a folder batch if connected; null when not connected. */
    suspend fun sendFolder(items: List<FolderItem>): List<TransferProgress>? {
        val ch = channel ?: return null
        return fileTransfer.sendFolder(ch, items, sendOptions())
    }

    override fun close() { channel?.close() }
}
