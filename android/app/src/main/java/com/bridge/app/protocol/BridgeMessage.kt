package com.bridge.app.protocol

import kotlinx.serialization.Serializable
import kotlinx.serialization.json.*
import java.util.UUID
import java.time.Instant

@Serializable
data class BridgeMessage(
    val protocol: Int = 1,
    val type: String,
    val id: String = UUID.randomUUID().toString(),
    val timestamp: String = Instant.now().toString(),
    val payload: JsonObject,
) {
    fun toJson(): String = Json.encodeToString(serializer(), this)

    inline fun <reified T> payloadAs(): T = Json.decodeFromJsonElement(payload)

    companion object {
        fun fromJson(json: String): BridgeMessage = Json.decodeFromString(serializer(), json)

        inline fun <reified T> create(type: String, payload: T): BridgeMessage {
            val node = Json.encodeToJsonElement(payload).jsonObject
            return BridgeMessage(type = type, payload = node)
        }
    }
}
