package com.bridge.app

import com.bridge.app.security.FileHasher
import com.bridge.app.security.IdentityKeys
import com.bridge.app.security.SessionCrypto
import java.security.GeneralSecurityException
import org.junit.Assert.assertArrayEquals
import org.junit.Assert.assertEquals
import org.junit.Assert.assertFalse
import org.junit.Assert.assertTrue
import org.junit.Assert.fail
import org.junit.Test

class CryptoTest {
    private fun pair(): Pair<SessionCrypto, SessionCrypto> {
        val initiator = SessionCrypto()
        val responder = SessionCrypto()
        val transcript = SessionCrypto.transcript("phone", initiator.ephemeralPublicKey, "pc", responder.ephemeralPublicKey)
        initiator.completeHandshake(responder.ephemeralPublicKey, transcript, isInitiator = true)
        responder.completeHandshake(initiator.ephemeralPublicKey, transcript, isInitiator = false)
        return initiator to responder
    }

    private inline fun assertRejected(block: () -> Unit) {
        try { block(); fail("expected the frame to be rejected") } catch (e: GeneralSecurityException) { }
    }

    @Test
    fun sessionCrypto_encryptDecrypt_roundTripsBothDirections() {
        val (initiator, responder) = pair()
        val plaintext = "bridge session payload".toByteArray()
        assertArrayEquals(plaintext, responder.decrypt(initiator.encrypt(plaintext)))
        assertArrayEquals(plaintext, initiator.decrypt(responder.encrypt(plaintext)))
    }

    @Test
    fun sessionCrypto_rejectsReplayReorderAndTamper() {
        val (initiator, responder) = pair()
        val first = initiator.encrypt(byteArrayOf(1))
        val second = initiator.encrypt(byteArrayOf(2))
        assertRejected { responder.decrypt(second) } // out of order
        responder.decrypt(first)
        assertRejected { responder.decrypt(first) } // replay
        second[SessionCrypto.NONCE_SIZE] = (second[SessionCrypto.NONCE_SIZE].toInt() xor 1).toByte()
        assertRejected { responder.decrypt(second) } // tampered
    }

    @Test
    fun sessionCrypto_reflectedFrameDoesNotDecrypt() {
        val (initiator, _) = pair()
        assertRejected { initiator.decrypt(initiator.encrypt(byteArrayOf(1, 2, 3))) }
    }

    @Test
    fun identity_signaturesVerifyOnlyWithMatchingKey() {
        val me = SoftwareIdentity()
        val other = SoftwareIdentity()
        val data = byteArrayOf(1, 2, 3)
        val sig = me.sign(data)
        assertEquals(0x30, sig[0].toInt()) // DER SEQUENCE, what .NET verifies with Rfc3279DerSequence
        assertTrue(IdentityKeys.verify(me.publicKeyBytes(), data, sig))
        assertFalse(IdentityKeys.verify(other.publicKeyBytes(), data, sig))
        assertFalse(IdentityKeys.verify(byteArrayOf(1, 2, 3), data, sig)) // garbage key never throws
    }

    @Test
    fun fileHasher_producesConsistentHash() {
        val data = "bridge file contents".toByteArray()
        assertEquals(FileHasher.sha256Hex(data), FileHasher.sha256Hex(data))
        assertEquals(64, FileHasher.sha256Hex(data).length)
        // Known-answer test, identical to the Windows test.
        assertEquals("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", FileHasher.sha256Hex("abc".toByteArray()))
    }
}
