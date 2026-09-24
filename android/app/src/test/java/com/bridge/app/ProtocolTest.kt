package com.bridge.app

import com.bridge.app.protocol.*
import org.junit.Assert.assertEquals
import org.junit.Test

class ProtocolTest {
    @Test
    fun clipboardMessage_roundTripsThroughJson() {
        val original = BridgeMessage.create(MessageType.CLIPBOARD_UPDATE, ClipboardUpdatePayload("text", "hello bridge"))
        val parsed = BridgeMessage.fromJson(original.toJson())
        val payload = parsed.payloadAs<ClipboardUpdatePayload>()

        assertEquals(MessageType.CLIPBOARD_UPDATE, parsed.type)
        assertEquals(1, parsed.protocol)
        assertEquals("hello bridge", payload.data)
    }

    @Test
    fun fileStartPayload_roundTrips() {
        val msg = BridgeMessage.create(MessageType.FILE_START, FileStartPayload("t1", "photo.jpg", 12345L, "abcd", 1))
        val parsed = BridgeMessage.fromJson(msg.toJson()).payloadAs<FileStartPayload>()

        assertEquals("photo.jpg", parsed.fileName)
        assertEquals(12345L, parsed.size)
        assertEquals(1, parsed.totalChunks)
    }
}
