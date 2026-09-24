package com.bridge.app

import com.bridge.app.security.FileHasher
import com.bridge.app.security.SessionCrypto
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Test

class CryptoTest {
    @Test
    fun sessionCrypto_encryptDecrypt_roundTripsBothDirections() {
        val initiator = SessionCrypto()
        val responder = SessionCrypto()

        initiator.completeHandshake(responder.ephemeralPublicKey, isInitiator = true)
        responder.completeHandshake(initiator.ephemeralPublicKey, isInitiator = false)

        val plaintext = "bridge session payload".toByteArray()
        val encrypted = initiator.encrypt(plaintext)
        val decrypted = responder.decrypt(encrypted)

        assertArrayEquals(plaintext, decrypted)
    }

    @Test
    fun fileHasher_producesConsistentHash() {
        val data = "bridge file contents".toByteArray()
        assertEquals(FileHasher.sha256Hex(data), FileHasher.sha256Hex(data))
        assertEquals(64, FileHasher.sha256Hex(data).length)
    }
}
