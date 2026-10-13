package com.bridge.app.network

import com.bridge.app.protocol.BridgeJson
import com.bridge.app.security.IdentityKeys
import java.io.ByteArrayOutputStream
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.SocketTimeoutException
import java.nio.ByteBuffer
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.ensureActive
import kotlinx.coroutines.withContext
import kotlinx.serialization.Serializable

/**
 * Local discovery wire format, mirroring Bridge.Network.DiscoveryWire on
 * Windows (protocol/protocol.md, "Discovery").
 */
object DiscoveryWire {
    const val DEFAULT_PORT = 47821
    const val MAX_DATAGRAM = 512
    const val NONCE_SIZE = 16
    const val TAG_SIZE = 16
    const val PROBE_KIND = "discover"
    const val REPLY_KIND = "here"
    const val VERSION = 1

    @Serializable data class Probe(val bridge: String, val v: Int, val nonce: String, val tag: String)
    @Serializable data class Reply(val bridge: String, val v: Int, val nonce: String, val deviceId: String, val port: Int, val sig: String)

    /** First 16 bytes of SHA-256( f("bridge-discovery-tag-v1") ‖ f(pcKey) ‖ f(nonce) ). */
    fun tag(pcPublicKey: ByteArray, nonce: ByteArray): ByteArray =
        MessageDigest.getInstance("SHA-256").digest(fields("bridge-discovery-tag-v1".toByteArray(), pcPublicKey, nonce)).copyOf(TAG_SIZE)

    /** Bytes the PC signs: f("bridge-discovery-v1") ‖ f(nonce) ‖ f(deviceId) ‖ f(port as 4-byte BE). */
    fun signedData(nonce: ByteArray, deviceId: String, port: Int): ByteArray =
        fields("bridge-discovery-v1".toByteArray(), nonce, deviceId.toByteArray(Charsets.UTF_8), ByteBuffer.allocate(4).putInt(port).array())

    fun buildProbe(pcPublicKey: ByteArray, nonce: ByteArray): ByteArray {
        val b64 = Base64.getEncoder()
        return BridgeJson.encodeToString(Probe.serializer(),
            Probe(PROBE_KIND, VERSION, b64.encodeToString(nonce), b64.encodeToString(tag(pcPublicKey, nonce)))).toByteArray(Charsets.UTF_8)
    }

    /** Returns the PC's TCP port if [datagram] is a genuine reply to [nonce] from the PC with [pcPublicKey]. */
    fun verifyReply(datagram: ByteArray, nonce: ByteArray, expectedDeviceId: String, pcPublicKey: ByteArray): Int? = try {
        if (datagram.size > MAX_DATAGRAM) null else {
            val reply = BridgeJson.decodeFromString(Reply.serializer(), String(datagram, Charsets.UTF_8))
            val dec = Base64.getDecoder()
            when {
                reply.bridge != REPLY_KIND || reply.v != VERSION -> null
                !MessageDigest.isEqual(dec.decode(reply.nonce), nonce) -> null
                reply.deviceId != expectedDeviceId || reply.port !in 1..65535 -> null
                !IdentityKeys.verify(pcPublicKey, signedData(nonce, reply.deviceId, reply.port), dec.decode(reply.sig)) -> null
                else -> reply.port
            }
        }
    } catch (e: Exception) {
        null // malformed JSON or base64: not a reply we can trust
    }

    /** 192.168.1.23/24 → 192.168.1.255. Null for /0, /31, /32 (no usable broadcast). */
    fun directedBroadcast(address: java.net.Inet4Address, prefixLength: Int): InetAddress? {
        if (prefixLength !in 1..30) return null
        val ip = address.address.fold(0) { acc, b -> (acc shl 8) or (b.toInt() and 0xff) }
        val bcast = ip or (-1 shl (32 - prefixLength)).inv()
        return InetAddress.getByAddress(byteArrayOf((bcast ushr 24).toByte(), (bcast ushr 16).toByte(), (bcast ushr 8).toByte(), bcast.toByte()))
    }

    private fun fields(vararg values: ByteArray): ByteArray {
        val out = ByteArrayOutputStream()
        for (v in values) {
            out.write(ByteBuffer.allocate(4).putInt(v.size).array())
            out.write(v)
        }
        return out.toByteArray()
    }
}

data class DiscoveredPc(val host: String, val port: Int)

/**
 * Phone side of discovery: broadcasts a probe for one paired PC and waits for
 * its signed reply. [targets] supplies where to send probes (the Wi-Fi
 * subnet's broadcast address and 255.255.255.255 on a phone; loopback in tests).
 */
open class PcDiscovery(
    private val targets: () -> List<InetAddress>,
    private val port: Int = DiscoveryWire.DEFAULT_PORT,
) {
    private val random = SecureRandom()

    open suspend fun find(pc: PairedDevice, timeoutMs: Long = 2_500): DiscoveredPc? = withContext(Dispatchers.IO) {
        val key = pc.publicKey()
        val nonce = ByteArray(DiscoveryWire.NONCE_SIZE).also(random::nextBytes)
        val probe = DiscoveryWire.buildProbe(key, nonce)
        val destinations = runCatching { targets() }.getOrDefault(emptyList()).distinct()
        if (destinations.isEmpty()) return@withContext null

        DatagramSocket(null as java.net.SocketAddress?).use { socket ->
            socket.broadcast = true
            // Room for a burst of junk on the LAN without the genuine reply being dropped.
            runCatching { socket.receiveBufferSize = 1 shl 20 }
            socket.bind(InetSocketAddress(0))
            socket.soTimeout = 150
            val buffer = ByteArray(DiscoveryWire.MAX_DATAGRAM + 1)
            val deadline = System.currentTimeMillis() + timeoutMs
            // UDP is lossy: resend a few times early on.
            val resendAt = longArrayOf(0, 300, 800).map { System.currentTimeMillis() + it }.toMutableList()
            while (System.currentTimeMillis() < deadline) {
                ensureActive()
                if (resendAt.isNotEmpty() && System.currentTimeMillis() >= resendAt.first()) {
                    resendAt.removeAt(0)
                    for (dest in destinations) {
                        runCatching { socket.send(DatagramPacket(probe, probe.size, dest, port)) } // unreachable target: try the others
                    }
                }
                val packet = DatagramPacket(buffer, buffer.size)
                try {
                    socket.receive(packet)
                } catch (e: SocketTimeoutException) {
                    continue
                }
                val data = packet.data.copyOf(packet.length)
                val tcpPort = DiscoveryWire.verifyReply(data, nonce, pc.deviceId, key) ?: continue // spoofed / stray: ignore
                return@withContext DiscoveredPc(packet.address.hostAddress ?: continue, tcpPort)
            }
            null
        }
    }
}
