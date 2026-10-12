package com.bridge.app.security

import android.content.Context
import android.security.keystore.KeyGenParameterSpec
import android.security.keystore.KeyProperties
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.PrivateKey
import java.security.Signature
import java.security.spec.ECGenParameterSpec
import java.util.UUID

/**
 * Long-term device identity. The P-256 private key is generated inside the
 * Android Keystore (non-exportable) for SIGN only; it signs the handshake
 * transcript with SHA256withECDSA, which the Windows side verifies. Only the
 * public key ever leaves the device.
 *
 * (The previous version requested PURPOSE_AGREE_KEY, which only exists on
 * API 31+, so key generation failed on Android 8–11 and crashed the app at
 * startup. Signing keys work on every supported API level.)
 */
class DeviceIdentity private constructor(override val deviceId: String, private val alias: String) : IdentityKey {

    private fun keyStore(): KeyStore = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

    override fun publicKeyBytes(): ByteArray =
        keyStore().getCertificate(alias).publicKey.encoded // X.509 SubjectPublicKeyInfo, same encoding Windows exports

    override fun sign(data: ByteArray): ByteArray {
        val privateKey = keyStore().getKey(alias, null) as PrivateKey
        return Signature.getInstance("SHA256withECDSA").run {
            initSign(privateKey)
            update(data)
            sign() // DER-encoded, as Windows expects
        }
    }

    companion object {
        private const val ANDROID_KEYSTORE = "AndroidKeyStore"
        private const val PREFS = "bridge_identity"
        private const val KEY_DEVICE_ID = "device_id"
        private const val KEY_ALIAS = "bridge_identity_ecdsa_v1"

        fun loadOrCreate(context: Context): DeviceIdentity {
            val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val existingId = prefs.getString(KEY_DEVICE_ID, null)
            val ks = KeyStore.getInstance(ANDROID_KEYSTORE).apply { load(null) }

            if (existingId != null && ks.containsAlias(KEY_ALIAS)) {
                return DeviceIdentity(existingId, KEY_ALIAS)
            }

            val deviceId = UUID.randomUUID().toString()
            val generator = KeyPairGenerator.getInstance(KeyProperties.KEY_ALGORITHM_EC, ANDROID_KEYSTORE)
            generator.initialize(
                KeyGenParameterSpec.Builder(KEY_ALIAS, KeyProperties.PURPOSE_SIGN or KeyProperties.PURPOSE_VERIFY)
                    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1")) // NIST P-256, matches Windows
                    .setDigests(KeyProperties.DIGEST_SHA256)
                    .build()
            )
            generator.generateKeyPair()

            prefs.edit().putString(KEY_DEVICE_ID, deviceId).apply()
            return DeviceIdentity(deviceId, KEY_ALIAS)
        }
    }
}
