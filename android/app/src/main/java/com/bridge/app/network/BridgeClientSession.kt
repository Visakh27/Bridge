package com.bridge.app.network

import com.bridge.app.protocol.*
import com.bridge.app.security.IdentityKey
import java.io.File
import java.io.IOException
import java.security.GeneralSecurityException
import kotlinx.coroutines.coroutineScope
import kotlinx.coroutines.delay
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
) {
    interface Listener {
        fun onConnected(peerFeatures: List<String>) {}
        fun onPeerError(error: ErrorPayload) {}
        fun onDisconnected() {}
    }

    companion object {
        val LOCAL_FEATURES = listOf(Features.FILES, Features.CLIPBOARD, Features.NOTIFICATIONS)
        private const val SETUP_TIMEOUT_MS = 20_000L
    }

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
    suspend fun run() {
        var peerFeatures = emptyList<String>()
        val ch = try { withTimeout(SETUP_TIMEOUT_MS) {
            val transport = connect(pc.host, pc.port)
            val ch = EncryptedChannel.handshakeAsInitiator(transport, identity, pc.publicKey())
            try {
                ch.send(BridgeMessage.create(MessageType.CAPABILITIES_ADVERTISE, CapabilitiesPayload(LOCAL_FEATURES)))
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
        listener.onConnected(peerFeatures)
        try {
            coroutineScope {
                val keepAlive = launch { keepAliveLoop(ch) }
                val status = launch { statusLoop(ch) }
                try {
                    receiveLoop(ch)
                } finally {
                    keepAlive.cancel()
                    status.cancel()
                }
            }
        } finally {
            channel = null
            if (clipboard.channel === ch) clipboard.channel = null
            ch.close()
            fileTransfer.abortAll()
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
                } catch (e: MalformedMessageException) {
                    // One bad message shouldn't drop the connection.
                } catch (e: IOException) {
                    // e.g. local storage error while saving a file; keep the session.
                    if (!ch.isConnected) throw e
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
            MessageType.FILE_CANCEL, MessageType.FILE_ACK ->
                fileTransfer.handleIncoming(ch, message, downloadDir)
            MessageType.PRESENCE_PING -> ch.send(BridgeMessage.createEmpty(MessageType.PRESENCE_PONG))
            MessageType.PRESENCE_PONG -> Unit // lastSeen already updated
            MessageType.CAPABILITIES_ADVERTISE ->
                clipboard.peerSupportsClipboard = Features.CLIPBOARD in message.payloadAs<CapabilitiesPayload>().features
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

    /** Sends a file if connected; null when not connected. */
    suspend fun sendFile(file: File, displayName: String = file.name): TransferProgress? {
        val ch = channel ?: return null
        return fileTransfer.sendFile(ch, file, displayName)
    }

    fun close() { channel?.close() }
}
