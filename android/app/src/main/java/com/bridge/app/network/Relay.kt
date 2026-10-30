package com.bridge.app.network

import com.bridge.app.protocol.BridgeMessage
import com.bridge.app.protocol.RelayConfigPayload
import java.io.IOException
import java.io.InputStream
import java.net.InetSocketAddress
import java.net.Socket
import java.security.MessageDigest
import java.security.SecureRandom
import java.util.Base64
import javax.crypto.Mac
import javax.crypto.spec.SecretKeySpec
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonObject
import kotlinx.serialization.json.JsonPrimitive
import kotlinx.serialization.json.contentOrNull
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.jsonPrimitive

/** The user's relay, as the PC sent it (relay.config). Wire-compatible with Bridge.Network.RelayLink. */
class RelayConfig(val host: String, val port: Int, val key: ByteArray) {
    override fun equals(other: Any?) = other is RelayConfig && host == other.host && port == other.port && key.contentEquals(other.key)
    override fun hashCode() = host.hashCode() * 31 + port
    override fun toString() = "RelayConfig($host:$port)" // never the key

    companion object {
        const val KEY_LENGTH = 32
        private val HOST_NAME = Regex("^(?=.{1,253}$)([A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)(\\.[A-Za-z0-9]([A-Za-z0-9-]{0,61}[A-Za-z0-9])?)*$")
        private val IPV6 = Regex("^[0-9A-Fa-f:.]{2,45}$")

        /** A relay.config that turns the relay on, checked like the PC checks a link; null if anything is off. */
        fun from(p: RelayConfigPayload): RelayConfig? {
            if (!p.enabled) return null
            val host = p.host?.trim()?.lowercase() ?: return null
            val port = p.port ?: return null
            val key = decodeKey(p.key) ?: return null
            if (port !in 1..65535) return null
            if (!HOST_NAME.matches(host) && !(host.contains(':') && IPV6.matches(host))) return null
            return RelayConfig(host, port, key)
        }

        fun decodeKey(text: String?): ByteArray? {
            if (text == null || text.length != 43 || !text.all { it.isLetterOrDigit() && it.code < 128 || it == '-' || it == '_' }) return null
            return runCatching { Base64.getUrlDecoder().decode(text) }.getOrNull()?.takeIf { it.size == KEY_LENGTH }
        }

        fun encodeKey(key: ByteArray): String = Base64.getUrlEncoder().withoutPadding().encodeToString(key)
    }
}

/** Why the relay turned the phone away ("pc_offline", "unauthorized", "busy", …). */
class RelayRefusedException(val code: String) : IOException("Relay refused: $code")

/** The relay's join protocol (see Bridge.Network.RelayProtocol and docs/remote.md). */
object RelayProtocol {
    const val VERSION = 1
    const val MAX_LINE = 1024
    private val random = SecureRandom()

    /** SHA-256 over both long-term public keys exactly as exchanged at pairing (PC's first). */
    fun room(pcPublicKey: ByteArray, phonePublicKey: ByteArray): String {
        val md = MessageDigest.getInstance("SHA-256")
        md.update("bridge-relay-room-v1".toByteArray(Charsets.US_ASCII))
        md.update(0)
        md.update(pcPublicKey)
        md.update(0)
        md.update(phonePublicKey)
        return md.digest().toHex()
    }

    fun proof(key: ByteArray, role: String, room: String, ts: Long, nonce: String): String {
        val mac = Mac.getInstance("HmacSHA256")
        mac.init(SecretKeySpec(key, "HmacSHA256"))
        return mac.doFinal("bridge-relay-v1\n$role\n$room\n$ts\n$nonce".toByteArray(Charsets.UTF_8)).toHex()
    }

    fun hello(key: ByteArray, role: String, room: String, nowMs: Long, nonce: String? = null): String {
        val n = nonce ?: ByteArray(16).also(random::nextBytes).toHex()
        val ts = nowMs / 1000
        return JsonObject(mapOf(
            "v" to JsonPrimitive(VERSION), "role" to JsonPrimitive(role), "room" to JsonPrimitive(room),
            "ts" to JsonPrimitive(ts), "nonce" to JsonPrimitive(n), "proof" to JsonPrimitive(proof(key, role, room, ts, n)),
        )).toString()
    }

    /** ("paired" | "waiting" | "ping" | "error" | "?", code). */
    fun parseReply(line: String): Pair<String, String?> = runCatching {
        val o = Json.parseToJsonElement(line).jsonObject
        (o["t"]?.jsonPrimitive?.contentOrNull ?: "?") to o["code"]?.jsonPrimitive?.contentOrNull
    }.getOrDefault("?" to null)

    /** Reads one '\n'-terminated line byte by byte, never past it (the pipe starts right after). */
    fun readLine(input: InputStream): String? {
        val buffer = java.io.ByteArrayOutputStream()
        while (true) {
            val b = input.read()
            if (b < 0) return if (buffer.size() == 0) null else throw IOException("Relay connection closed mid-line")
            if (b == '\n'.code) return buffer.toString(Charsets.UTF_8.name())
            if (buffer.size() >= MAX_LINE) throw IOException("Relay line too long")
            buffer.write(b)
        }
    }

    private fun ByteArray.toHex(): String = joinToString("") { "%02x".format(it) }
}

/** Joins the relay as the phone: the pipe to the waiting PC, or [RelayRefusedException]. */
object RelayClient {
    private const val CONNECT_TIMEOUT_MS = 10_000
    private const val REPLY_TIMEOUT_MS = 10_000

    suspend fun join(config: RelayConfig, room: String, nowMs: () -> Long = System::currentTimeMillis): TcpTransport = withContext(Dispatchers.IO) {
        val socket = Socket()
        try {
            socket.tcpNoDelay = true
            socket.connect(InetSocketAddress(config.host, config.port), CONNECT_TIMEOUT_MS)
            socket.soTimeout = REPLY_TIMEOUT_MS
            val out = socket.getOutputStream()
            out.write((RelayProtocol.hello(config.key, "phone", room, nowMs()) + "\n").toByteArray(Charsets.UTF_8))
            out.flush()
            val input = socket.getInputStream()
            while (true) {
                val line = RelayProtocol.readLine(input) ?: throw IOException("Relay closed the connection")
                val (type, code) = RelayProtocol.parseReply(line)
                when (type) {
                    "paired" -> { socket.soTimeout = 0; return@withContext TcpTransport(socket) }
                    "ping", "waiting" -> Unit
                    "error" -> throw RelayRefusedException(code?.takeIf { c -> c.length <= 32 && c.all { it in 'a'..'z' || it == '_' } } ?: "unknown")
                    else -> throw IOException("Unexpected reply from the relay")
                }
            }
            @Suppress("UNREACHABLE_CODE") throw IOException("unreachable")
        } catch (e: Exception) {
            runCatching { socket.close() }
            throw e
        }
    }
}

/** Where each PC's relay is kept (SharedPreferences on a phone). */
interface RelayConfigStore {
    fun get(pcId: String): RelayConfig?
    fun put(pcId: String, config: RelayConfig?)
}

class InMemoryRelayConfigStore : RelayConfigStore {
    private val map = java.util.concurrent.ConcurrentHashMap<String, RelayConfig>()
    override fun get(pcId: String) = map[pcId]
    override fun put(pcId: String, config: RelayConfig?) { if (config == null) map.remove(pcId) else map[pcId] = config }
}

/** Applies relay.config from the PC this session is with. */
class RelayConfigHandler(private val store: RelayConfigStore, private val onChanged: ((pcId: String, on: Boolean) -> Unit)? = null) {
    /** Returns whether the message was valid. A malformed "on" config leaves the stored one alone. */
    fun handle(pcId: String, message: BridgeMessage): Boolean {
        val p = runCatching { message.payloadAs<RelayConfigPayload>() }.getOrNull() ?: return false
        if (!p.enabled) { store.put(pcId, null); onChanged?.invoke(pcId, false); return true }
        val config = RelayConfig.from(p) ?: return false
        store.put(pcId, config)
        onChanged?.invoke(pcId, true)
        return true
    }
}
