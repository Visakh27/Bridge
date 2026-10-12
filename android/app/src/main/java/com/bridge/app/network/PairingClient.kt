package com.bridge.app.network

import com.bridge.app.protocol.*
import com.bridge.app.security.IdentityKey
import java.io.IOException
import java.security.GeneralSecurityException
import java.util.Base64
import kotlinx.coroutines.TimeoutCancellationException
import kotlinx.coroutines.withTimeout
import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException

/** The PC this phone is paired with, plus where to reach it. */
@Serializable
data class PairedDevice(
    val deviceId: String,
    val deviceName: String,
    val longTermPublicKeyBase64: String,
    val host: String,
    val port: Int,
) {
    fun publicKey(): ByteArray = Base64.getDecoder().decode(longTermPublicKeyBase64)
}

data class PairingResult(val device: PairedDevice)

class PairingException(message: String, cause: Throwable? = null) : Exception(message, cause)

/**
 * Android side of pairing (section 6): parses the QR payload the user
 * scanned, opens a TCP connection to the PC's advertised address, verifies
 * the PC is the one that showed the QR (its handshake signature must match
 * the key in the QR) and only then sends pairing.request with the token.
 */
class PairingClient(
    private val identity: IdentityKey,
    private val store: PairedDeviceStore,
    private val connect: suspend (host: String, port: Int) -> TcpTransport = TcpTransport::connect,
    private val timeoutMs: Long = 20_000,
) {

    suspend fun pairFromQrContent(qrJson: String, deviceName: String): Result<PairingResult> = runCatching {
        val qr = try {
            BridgeJson.decodeFromString(PairingQrPayload.serializer(), qrJson)
        } catch (e: SerializationException) {
            throw PairingException("That isn't a Bridge pairing code", e)
        } catch (e: IllegalArgumentException) {
            throw PairingException("That isn't a Bridge pairing code", e)
        }
        val pcKey = try { Base64.getDecoder().decode(qr.publicKeyBase64) } catch (e: IllegalArgumentException) {
            throw PairingException("That isn't a Bridge pairing code", e)
        }

        try {
            withTimeout(timeoutMs) { pair(qr, pcKey, deviceName) }
        } catch (e: AuthenticationException) {
            throw PairingException("Couldn't verify the PC. Make sure you scanned the code on your own PC.", e)
        } catch (e: TimeoutCancellationException) {
            throw PairingException("The PC didn't respond. Make sure Bridge is open on ${qr.deviceName}.", e)
        } catch (e: IOException) {
            // Raw socket errors are never shown to the user (docs/architecture.md).
            throw PairingException("Couldn't reach ${qr.deviceName}. Make sure Bridge is open on the PC and both devices are on the same Wi-Fi.", e)
        } catch (e: GeneralSecurityException) {
            throw PairingException("The connection to the PC was interrupted. Please try again.", e)
        } catch (e: MalformedMessageException) {
            throw PairingException("The PC sent an unexpected reply. Make sure both apps are up to date.", e)
        }
    }

    private suspend fun pair(qr: PairingQrPayload, pcKey: ByteArray, deviceName: String): PairingResult {
        val transport = connect(qr.host, qr.port)
        val channel = EncryptedChannel.handshakeAsInitiator(transport, identity, pcKey)

        try {
            if (channel.peerDeviceId != qr.deviceId) throw PairingException("Connected to a different PC than the code belongs to")

            val publicKeyB64 = Base64.getEncoder().encodeToString(identity.publicKeyBytes())
            channel.send(BridgeMessage.create(MessageType.PAIRING_REQUEST,
                PairingRequestPayload(qr.pairingToken, deviceName, publicKeyB64)))

            val response = channel.receive()
            when (response.type) {
                MessageType.PAIRING_ACCEPT -> {
                    val accept = response.payloadAs<PairingAcceptPayload>()
                    if (accept.publicKeyBase64 != qr.publicKeyBase64) throw PairingException("PC identity changed during pairing")
                    val device = PairedDevice(qr.deviceId, accept.deviceName, qr.publicKeyBase64, qr.host, qr.port)
                    store.save(device)
                    return PairingResult(device)
                }
                MessageType.PAIRING_REJECT -> throw PairingException(response.payloadAs<PairingRejectPayload>().reason)
                else -> throw PairingException("Unexpected response during pairing: ${response.type}")
            }
        } finally {
            channel.close() // pairing connections are one-shot; the session reconnects
        }
    }
}

interface PairedDeviceStore {
    suspend fun save(device: PairedDevice)
    suspend fun list(): List<PairedDevice>
    suspend fun revoke(deviceId: String)
}
