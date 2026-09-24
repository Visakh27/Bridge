package com.bridge.app.network

import com.bridge.app.protocol.*
import com.bridge.app.security.DeviceIdentity
import kotlinx.serialization.json.Json
import java.util.Base64

data class PairedDevice(val deviceId: String, val deviceName: String, val longTermPublicKeyBase64: String)
data class PairingResult(val device: PairedDevice, val host: String, val port: Int)

/**
 * Android side of pairing (section 6): parses the QR payload the user
 * scanned, opens a TCP connection to the PC's advertised port, completes the
 * encrypted handshake, and sends pairing.request.
 */
class PairingClient(private val identity: DeviceIdentity, private val store: PairedDeviceStore) {

    suspend fun pairFromQrContent(qrJson: String, deviceName: String): Result<PairingResult> = runCatching {
        val qr = Json.decodeFromString(PairingQrPayload.serializer(), qrJson)

        val transport = TcpTransport.connect(qr.host, qr.port)
        val channel = EncryptedChannel.handshakeAsInitiator(transport, identity.deviceId)

        val publicKeyB64 = Base64.getEncoder().encodeToString(identity.publicKeyBytes())
        channel.send(
            BridgeMessage.create(
                MessageType.PAIRING_REQUEST,
                PairingRequestPayload(qr.pairingToken, deviceName, publicKeyB64)
            )
        )

        val response = channel.receive()
        when (response.type) {
            MessageType.PAIRING_ACCEPT -> {
                val accept = response.payloadAs<PairingAcceptPayload>()
                val device = PairedDevice(qr.deviceId, accept.deviceName, accept.publicKeyBase64)
                store.save(device)
                channel.close()
                PairingResult(device, qr.host, qr.port)
            }
            MessageType.PAIRING_REJECT -> {
                val reject = response.payloadAs<PairingRejectPayload>()
                channel.close()
                throw IllegalStateException("Pairing rejected: ${reject.reason}")
            }
            else -> {
                channel.close()
                throw IllegalStateException("Unexpected response during pairing: ${response.type}")
            }
        }
    }
}

interface PairedDeviceStore {
    suspend fun save(device: PairedDevice)
    suspend fun list(): List<PairedDevice>
    suspend fun revoke(deviceId: String)
}
