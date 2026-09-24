package com.bridge.app.security

import android.content.Context
import java.security.KeyPairGenerator
import java.security.KeyStore
import java.security.spec.ECGenParameterSpec
import java.util.UUID

/**
 * Long-term device identity. The private key is generated inside the Android
 * Keystore (non-exportable) via KeyStore-backed EC key generation; only the
 * public key ever leaves the device, mirroring Bridge.Security.DeviceIdentity
 * on Windows (which uses DPAPI instead of a hardware-backed keystore).
 */
class DeviceIdentity private constructor(val deviceId: String, val alias: String) {

    fun publicKeyBytes(): ByteArray {
        val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }
        val cert = ks.getCertificate(alias)
        return cert.publicKey.encoded // X.509 SubjectPublicKeyInfo, same encoding Windows exports
    }

    companion object {
        private const val PREFS = "bridge_identity"
        private const val KEY_DEVICE_ID = "device_id"
        private const val KEY_ALIAS = "bridge_identity_key"

        fun loadOrCreate(context: Context): DeviceIdentity {
            val prefs = context.getSharedPreferences(PREFS, Context.MODE_PRIVATE)
            val existingId = prefs.getString(KEY_DEVICE_ID, null)
            val ks = KeyStore.getInstance("AndroidKeyStore").apply { load(null) }

            if (existingId != null && ks.containsAlias(KEY_ALIAS)) {
                return DeviceIdentity(existingId, KEY_ALIAS)
            }

            val deviceId = UUID.randomUUID().toString()
            val generator = KeyPairGenerator.getInstance("EC", "AndroidKeyStore")
            generator.initialize(
                android.security.keystore.KeyGenParameterSpec.Builder(
                    KEY_ALIAS,
                    android.security.keystore.KeyProperties.PURPOSE_AGREE_KEY
                )
                    .setAlgorithmParameterSpec(ECGenParameterSpec("secp256r1")) // NIST P-256, matches Windows
                    .build()
            )
            generator.generateKeyPair()

            prefs.edit().putString(KEY_DEVICE_ID, deviceId).apply()
            return DeviceIdentity(deviceId, KEY_ALIAS)
        }
    }
}
