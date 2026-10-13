package com.bridge.app.security

import java.security.KeyFactory
import java.security.Signature
import java.security.interfaces.ECPublicKey
import java.security.spec.X509EncodedKeySpec

/**
 * A device's long-term identity: an id plus a NIST P-256 key that signs the
 * handshake transcript with ECDSA-SHA256 (DER), matching
 * Bridge.Security.DeviceIdentity on Windows. On a phone this is backed by the
 * Android Keystore ([DeviceIdentity]); tests use a software key.
 */
interface IdentityKey {
    val deviceId: String
    /** X.509 SubjectPublicKeyInfo, the same encoding Windows exports. */
    fun publicKeyBytes(): ByteArray
    fun sign(data: ByteArray): ByteArray
}

object IdentityKeys {
    /** Verifies a DER ECDSA-SHA256 signature against a SubjectPublicKeyInfo key. Never throws. */
    fun verify(publicKeySpki: ByteArray, data: ByteArray, signature: ByteArray): Boolean = try {
        val key = KeyFactory.getInstance("EC").generatePublic(X509EncodedKeySpec(publicKeySpki))
        if (key !is ECPublicKey || key.params.curve.field.fieldSize != 256) {
            false
        } else {
            Signature.getInstance("SHA256withECDSA").run {
                initVerify(key)
                update(data)
                verify(signature)
            }
        }
    } catch (e: Exception) {
        false
    }
}
