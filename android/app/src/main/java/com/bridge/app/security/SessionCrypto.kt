package com.bridge.app.security

import java.security.KeyPairGenerator
import java.security.KeyFactory
import java.security.spec.ECGenParameterSpec
import java.security.spec.X509EncodedKeySpec
import javax.crypto.Cipher
import javax.crypto.KeyAgreement
import javax.crypto.spec.GCMParameterSpec
import javax.crypto.spec.SecretKeySpec
import java.security.MessageDigest
import java.security.SecureRandom

/**
 * Ephemeral ECDH (P-256) + HKDF-SHA256 + AES-256-GCM session layer, wire
 * compatible with Bridge.Security.SessionCrypto on Windows (see
 * protocol/protocol.md handshake section).
 */
class SessionCrypto {
    private val keyPair = KeyPairGenerator.getInstance("EC").apply {
        initialize(ECGenParameterSpec("secp256r1"))
    }.generateKeyPair()

    val ephemeralPublicKey: ByteArray get() = keyPair.public.encoded // X.509 SPKI, matches .NET's ExportSubjectPublicKeyInfo

    private var sendKey: ByteArray? = null
    private var recvKey: ByteArray? = null

    fun completeHandshake(peerEphemeralPublicKey: ByteArray, isInitiator: Boolean) {
        val kf = KeyFactory.getInstance("EC")
        val peerKey = kf.generatePublic(X509EncodedKeySpec(peerEphemeralPublicKey))

        val agreement = KeyAgreement.getInstance("ECDH")
        agreement.init(keyPair.private)
        agreement.doPhase(peerKey, true)
        val sharedSecret = agreement.generateSecret()

        val toDevice = hkdfSha256(sharedSecret, "bridge:win->android".toByteArray())
        val toPc = hkdfSha256(sharedSecret, "bridge:android->win".toByteArray())

        sendKey = if (isInitiator) toDevice else toPc
        recvKey = if (isInitiator) toPc else toDevice
    }

    fun encrypt(plaintext: ByteArray): ByteArray {
        val key = sendKey ?: error("Handshake not complete")
        val nonce = ByteArray(12).also { SecureRandom().nextBytes(it) }
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.ENCRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
        val ciphertextAndTag = cipher.doFinal(plaintext) // BC/JCE appends the 16-byte tag
        return nonce + ciphertextAndTag
    }

    fun decrypt(framed: ByteArray): ByteArray {
        val key = recvKey ?: error("Handshake not complete")
        val nonce = framed.copyOfRange(0, 12)
        val ciphertextAndTag = framed.copyOfRange(12, framed.size)
        val cipher = Cipher.getInstance("AES/GCM/NoPadding")
        cipher.init(Cipher.DECRYPT_MODE, SecretKeySpec(key, "AES"), GCMParameterSpec(128, nonce))
        return cipher.doFinal(ciphertextAndTag)
    }

    /** RFC 5869 HKDF (extract + single-block expand) producing a 32-byte key, matching .NET's HKDF.DeriveKey. */
    private fun hkdfSha256(ikm: ByteArray, info: ByteArray): ByteArray {
        val hmac = javax.crypto.Mac.getInstance("HmacSHA256")
        val salt = ByteArray(32) // zero salt, matching System.Security.Cryptography.HKDF default
        hmac.init(SecretKeySpec(salt, "HmacSHA256"))
        val prk = hmac.doFinal(ikm)

        hmac.init(SecretKeySpec(prk, "HmacSHA256"))
        hmac.update(info)
        hmac.update(1.toByte())
        return hmac.doFinal().copyOfRange(0, 32)
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
