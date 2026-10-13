package com.bridge.app

import com.bridge.app.security.IdentityKey
import java.security.KeyPairGenerator
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.UUID

/** JVM stand-in for the Keystore-backed DeviceIdentity (same curve, hash and DER encoding). */
class SoftwareIdentity(override val deviceId: String = UUID.randomUUID().toString()) : IdentityKey {
    private val keyPair = KeyPairGenerator.getInstance("EC").apply { initialize(ECGenParameterSpec("secp256r1")) }.generateKeyPair()
    override fun publicKeyBytes(): ByteArray = keyPair.public.encoded
    override fun sign(data: ByteArray): ByteArray = Signature.getInstance("SHA256withECDSA").run {
        initSign(keyPair.private); update(data); sign()
    }
}
