package com.bridge.app.ui

import android.content.Intent
import android.os.Bundle
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.*
import com.bridge.app.core.BridgeApplication
import com.bridge.app.network.PairingClient
import com.bridge.app.service.BridgeConnectionService

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val identity = (application as BridgeApplication).identity
        // A production build wires a real Room-backed PairedDeviceStore; using
        // an in-memory placeholder here keeps this screen runnable standalone.
        val store = InMemoryPairedDeviceStore()
        val pairingClient = PairingClient(identity, store)

        setContent {
            MaterialTheme {
                Surface {
                    var paired by remember { mutableStateOf(false) }

                    if (!paired) {
                        PairingScreen(pairingClient = pairingClient, deviceName = android.os.Build.MODEL) { host, port ->
                            paired = true
                            val intent = Intent(this, BridgeConnectionService::class.java)
                                .putExtra("host", host)
                                .putExtra("port", port)
                            startForegroundService(intent)
                        }
                    } else {
                        DashboardScreen()
                    }
                }
            }
        }
    }
}

private class InMemoryPairedDeviceStore : com.bridge.app.network.PairedDeviceStore {
    private val devices = mutableListOf<com.bridge.app.network.PairedDevice>()
    override suspend fun save(device: com.bridge.app.network.PairedDevice) { devices.add(device) }
    override suspend fun list() = devices.toList()
    override suspend fun revoke(deviceId: String) { devices.removeAll { it.deviceId == deviceId } }
}
