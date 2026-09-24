package com.bridge.app.ui

import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.unit.dp

/**
 * Section 4.2 (connected state): shows connection status and the clipboard
 * sync toggle. File sending from Android happens via the share sheet
 * (see AndroidManifest's SEND intent-filter), not from this screen.
 */
@Composable
fun DashboardScreen() {
    var clipboardSyncEnabled by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize().padding(24.dp)) {
        Text("Connected to PC", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(16.dp))

        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Sync clipboard", modifier = Modifier.weight(1f))
            Switch(checked = clipboardSyncEnabled, onCheckedChange = {
                clipboardSyncEnabled = it
                // In a full implementation this toggles BridgeConnectionService's
                // ClipboardSyncClient.enabled via a bound-service call.
            })
        }

        Spacer(Modifier.height(24.dp))
        Text(
            "Share a file to Bridge from any app to send it to your PC.",
            style = MaterialTheme.typography.bodyMedium
        )
    }
}
