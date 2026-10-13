package com.bridge.app

import com.bridge.app.network.SafeFileName
import com.bridge.app.protocol.*
import java.io.File
import java.nio.file.Files
import kotlinx.serialization.json.jsonObject
import kotlinx.serialization.json.Json
import org.junit.Assert.assertEquals
import org.junit.Assert.assertTrue
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

    @Test
    fun envelope_alwaysCarriesEveryRequiredField() {
        // message.schema.json requires protocol, type, id, timestamp, payload.
        val json = Json.parseToJsonElement(BridgeMessage.createEmpty(MessageType.PRESENCE_PONG).toJson()).jsonObject
        assertEquals(listOf("protocol", "type", "id", "timestamp", "payload"), json.keys.toList())
        assertEquals("{}", json["payload"].toString())
    }

    @Test
    fun parsesWindowsStyleMessage_andIgnoresUnknownKeys() {
        val json = """{"protocol":1,"type":"file.start","id":"e35840d2-ee67-4250-be9b-2fa2cb44e4b0","timestamp":"2026-10-12T07:44:39.3615737+00:00","payload":{"transferId":"t1","fileName":"photo.jpg","size":5,"sha256":"abcd","totalChunks":1,"futureField":true},"futureEnvelope":1}"""
        val meta = BridgeMessage.fromJson(json).payloadAs<FileStartPayload>()
        assertEquals("t1", meta.transferId)
        assertEquals(5L, meta.size)
    }

    @Test
    fun parsesWindowsQrPayload() {
        val qr = """{"pairingToken":"T/9Uze8j","host":"192.168.1.20","port":47820,"deviceId":"pc-id","deviceName":"OFFICE-PC","publicKeyBase64":"AAAA"}"""
        val parsed = BridgeJson.decodeFromString(PairingQrPayload.serializer(), qr)
        assertEquals(47820, parsed.port)
        assertEquals("OFFICE-PC", parsed.deviceName)
    }

    @Test
    fun malformedMessages_raiseMalformedMessageException() {
        for (bad in listOf("not json", """{"type":"x"}""", """{"protocol":1,"type":"file.ack","payload":{"transferId":1}}""")) {
            try {
                BridgeMessage.fromJson(bad).payloadAs<FileAckPayload>()
                throw AssertionError("expected failure for $bad")
            } catch (e: MalformedMessageException) { }
        }
    }

    @Test
    fun safeFileName_blocksTraversal() {
        assertEquals("passwd", SafeFileName.sanitize("../../../etc/passwd"))
        assertEquals("x.xml", SafeFileName.sanitize("..\\..\\shared_prefs\\x.xml"))
        assertEquals("received_file", SafeFileName.sanitize(".."))
        assertEquals("received_file", SafeFileName.sanitize(null))
        assertEquals("a_b.txt", SafeFileName.sanitize("a:b.txt"))
        assertEquals("photo.jpg", SafeFileName.sanitize("photo.jpg"))

        val dir = Files.createTempDirectory("bridge").toFile()
        File(dir, "a.txt").writeText("x")
        assertEquals("a (1).txt", SafeFileName.uniqueFile(dir, "a.txt").name)
        assertEquals("b.txt", SafeFileName.uniqueFile(dir, "b.txt").name)
        assertTrue(dir.deleteRecursively())
    }
}
