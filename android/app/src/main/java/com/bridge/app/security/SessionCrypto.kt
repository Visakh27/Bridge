package com.bridge.app.security

import java.io.ByteArrayOutputStream
import java.nio.ByteBuffer
import java.security.GeneralSecurityException
import java.security.KeyFactory
import java.security.KeyPairGenerator
import java.security.MessageDigest
import java.security.interfaces.ECPublicKey
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.Mac
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec

/**
 * Ephemeral ECDH (P-256) + HKDF-SHA256 + AES-256-GCM session layer, wire
 * compatible with Bridge.Security.SessionCrypto on Windows (see
 * protocol/protocol.md, Handshake).
 *
 * HKDF salt is the handshake transcript hash, binding the keys to both device
 * ids and both ephemeral keys. Nonces are a per-direction 64-bit counter
 * (4 zero bytes + big-endian counter): the receiver rejects any replayed,
 * dropped or reordered frame.
 */
class SessionCrypto {
    private val keyPair = KeyPairGenerator.getInstance("EC").apply {
        initialize(ECGenParameterSpec("secp256r1"))
    }.generateKeyPair()

    /** X.509 SPKI, matches .NET's ExportSubjectPublicKeyInfo. */
    val ephemeralPublicKey: ByteArray get() = keyPair.public.encoded

    private var sendKey: SecretKeySpec? = null
    private var recvKey: SecretKeySpec? = null
    private var sendCounter = 0L
    private var recvCounter = 0L

    fun completeHandshake(peerEphemeralPublicKey: ByteArray, transcript: ByteArray, isInitiator: Boolean) {
        val peerKey = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(peerEphemeralPublicKey))
        if (peerKey !is ECPublicKey || peerKey.params.curve.field.fieldSize != 256) {
            throw GeneralSecurityException("Peer ephemeral key is not P-256")
        }

        val agreement = KeyAgreement.getInstance("ECDH")
        agreement.init(keyPair.private)
        agreement.doPhase(peerKey, true)
        val sharedSecret = agreement.generateSecret() // raw x-coordinate, same as .NET DeriveRawSecretAgreement

        val i2r = hkdfSha256(sharedSecret, transcript, INITIATOR_TO_RESPONDER.toByteArray(Charsets.UTF_8))
        val r2i = hkdfSha256(sharedSecret, transcript, RESPONDER_TO_INITIATOR.toByteArray(Charsets.UTF_8))
        sharedSecret.fill(0)

        sendKey = SecretKeySpec(if (isInitiator) i2r else r2i, "AES")
        recvKey = SecretKeySpec(if (isInitiator) r2i else i2r, "AES")
        i2r.fill(0)
        r2i.fill(0)
    }

    /** Returns nonce || ciphertext || tag. Callers must serialise encrypt + send so frames hit the wire in counter order. */
    fun encrypt(plaintext: ByteArray): ByteArray {
        val key = sendKey ?: error("Handshake not complete")
        val nonce = nonceFor(sendCounter++)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, key, GCMParameterSpec(TAG_BITS, nonce))
        return nonce + cipher.doFinal(plaintext) // JCE appends the 16-byte tag
    }

    fun decrypt(framed: ByteArray): ByteArray {
        val key = recvKey ?: error("Handshake not complete")
        if (framed.size < NONCE_SIZE + TAG_BITS / 8) throw GeneralSecurityException("Frame too short")
        val nonce = framed.copyOfRange(0, NONCE_SIZE)
        if (!nonce.contentEquals(nonceFor(recvCounter))) {
            throw GeneralSecurityException("Unexpected nonce (replayed, dropped or reordered frame)")
        }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, key, GCMParameterSpec(TAG_BITS, nonce))
        val plaintext = cipher.doFinal(framed, NONCE_SIZE, framed.size - NONCE_SIZE)
        recvCounter++
        return plaintext
    }

    companion object {
        const val NONCE_SIZE = 12
        private const val TAG_BITS = 128
        private const val INITIATOR_TO_RESPONDER = "bridge-v1 initiator->responder"
        private const val RESPONDER_TO_INITIATOR = "bridge-v1 responder->initiator"
        private const val TRANSCRIPT_LABEL = "bridge-handshake-v1"

        /**
         * SHA-256 over the label and each field length-prefixed (4-byte
         * big-endian): initiator id, initiator key, responder id, responder key.
         * Both peers sign this with their long-term identity key.
         */
        fun transcript(initiatorId: String, initiatorEphemeral: ByteArray, responderId: String, responderEphemeral: ByteArray): ByteArray {
            val out = ByteArrayOutputStream()
            fun field(value: ByteArray) {
                out.write(ByteBuffer.allocate(4).putInt(value.size).array())
                out.write(value)
            }
            field(TRANSCRIPT_LABEL.toByteArray(Charsets.UTF_8))
            field(initiatorId.toByteArray(Charsets.UTF_8))
            field(initiatorEphemeral)
            field(responderId.toByteArray(Charsets.UTF_8))
            field(responderEphemeral)
            return MessageDigest.getInstance("SHA-256").digest(out.toByteArray())
        }

        private fun nonceFor(counter: Long): ByteArray =
            ByteBuffer.allocate(NONCE_SIZE).putInt(0).putLong(counter).array()

        /** RFC 5869 HKDF (extract + single-block expand) producing a 32-byte key, matching .NET's HKDF.DeriveKey. */
        private fun hkdfSha256(ikm: ByteArray, salt: ByteArray, info: ByteArray): ByteArray {
            val hmac = Mac.getInstance("HmacSHA256")
            hmac.init(SecretKeySpec(salt, "HmacSHA256"))
            val prk = hmac.doFinal(ikm)

            hmac.init(SecretKeySpec(prk, "HmacSHA256"))
            hmac.update(info)
            hmac.update(1.toByte())
            return hmac.doFinal().copyOfRange(0, 32)
        }
    }
}

object FileHasher {
    fun sha256Hex(data: ByteArray): String =
        MessageDigest.getInstance("SHA-256").digest(data).joinToString("") { "%02x".format(it) }

    fun sha256HexOfFile(path: String): String {
        val digest = MessageDigest.getInstance("SHA-256")
        java.io.File(path).inputStream().use { input ->
            val buffer = ByteArray(64 * 1024)
            var read: Int
            while (input.read(buffer).also { read = it } != -1) digest.update(buffer, 0, read)
        }
        return digest.digest().joinToString("") { "%02x".format(it) }
    }
}
