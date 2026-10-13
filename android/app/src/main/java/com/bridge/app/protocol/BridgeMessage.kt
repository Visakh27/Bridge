package com.bridge.app.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.SerializationException
import kotlinx.serialization.json.*
import java.time.Instant
import java.util.UUID

/**
 * The one JSON configuration for everything on the wire. `encodeDefaults`
 * makes sure `protocol` (and the other defaulted envelope fields) are always
 * sent, as message.schema.json requires; `ignoreUnknownKeys` lets a newer
 * Windows peer add payload fields without breaking this client.
 */
@OptIn(kotlinx.serialization.ExperimentalSerializationApi::class)
val BridgeJson: Json = Json {
    encodeDefaults = true
    ignoreUnknownKeys = true
    explicitNulls = false // optional fields that are null are left off the wire, like the Windows side
}

@Serializable
data class BridgeMessage(
    val protocol: Int = CURRENT_PROTOCOL,
    val type: String,
    val id: String = UUID.randomUUID().toString(),
    val timestamp: String = Instant.now().toString(),
    val payload: JsonObject,
) {
    fun toJson(): String = BridgeJson.encodeToString(serializer(), this)

    /** @throws MalformedMessageException if the payload doesn't match [T]. */
    inline fun <reified T> payloadAs(): T = try {
        BridgeJson.decodeFromJsonElement(payload)
    } catch (e: SerializationException) {
        throw MalformedMessageException("Malformed $type payload", e)
    } catch (e: IllegalArgumentException) {
        throw MalformedMessageException("Malformed $type payload", e)
    }

    companion object {
        const val CURRENT_PROTOCOL = 1

        /** @throws MalformedMessageException on invalid JSON or missing fields. */
        fun fromJson(json: String): BridgeMessage = try {
            BridgeJson.decodeFromString(serializer(), json)
        } catch (e: SerializationException) {
            throw MalformedMessageException("Malformed Bridge message", e)
        } catch (e: IllegalArgumentException) {
            throw MalformedMessageException("Malformed Bridge message", e)
        }

        inline fun <reified T> create(type: String, payload: T): BridgeMessage =
            BridgeMessage(type = type, payload = BridgeJson.encodeToJsonElement(payload).jsonObject)

        /** A message with an empty `{}` payload (presence.ping / presence.pong). */
        fun createEmpty(type: String): BridgeMessage = BridgeMessage(type = type, payload = JsonObject(emptyMap()))
    }
}

class MalformedMessageException(message: String, cause: Throwable? = null) : Exception(message, cause)
