package com.bridge.app.ui

import android.content.ComponentName
import android.content.Intent
import android.provider.Settings
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import androidx.core.app.NotificationManagerCompat
import com.bridge.app.notifications.BridgeNotificationListener
import com.bridge.app.service.BridgeConnectionService

/**
 * Section 4.2 (connected state): connection status, the clipboard sync
 * toggle, notification-mirroring access, and forgetting the PC. File sending
 * from Android happens via the share sheet (see MainActivity), not here.
 */
@Composable
fun DashboardScreen(state: BridgeConnectionService.State, onForget: () -> Unit) {
    val context = LocalContext.current
    var clipboardSyncEnabled by remember { mutableStateOf(BridgeConnectionService.isClipboardSyncEnabled(context)) }
    val notificationAccess = remember(state) {
        NotificationManagerCompat.getEnabledListenerPackages(context).contains(context.packageName)
    }
    var confirmForget by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize().padding(24.dp)) {
        val pc = state.pcName ?: "your PC"
        val title = when (state.status) {
            BridgeConnectionService.Status.CONNECTED -> "Connected to $pc"
            BridgeConnectionService.Status.CONNECTING -> "Connecting to $pc…"
            else -> "Not connected"
        }
        Text(title, style = MaterialTheme.typography.headlineSmall)
        if (state.status == BridgeConnectionService.Status.CONNECTING) {
            Spacer(Modifier.height(4.dp))
            Text("Make sure Bridge is open on the PC and both devices are on the same Wi-Fi.", style = MaterialTheme.typography.bodySmall)
        }
        state.lastTransfer?.let {
            Spacer(Modifier.height(8.dp))
            Text(it, style = MaterialTheme.typography.bodyMedium)
        }
        Spacer(Modifier.height(16.dp))

        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Sync clipboard", modifier = Modifier.weight(1f))
            Switch(checked = clipboardSyncEnabled, onCheckedChange = {
                clipboardSyncEnabled = it
                BridgeConnectionService.setClipboardSyncEnabled(context, it)
            })
        }
        Text("Copies from your phone are sent while Bridge is open (an Android privacy rule).",
            style = MaterialTheme.typography.bodySmall)

        Spacer(Modifier.height(16.dp))
        if (!notificationAccess) {
            OutlinedButton(onClick = {
                context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS).apply {
                    putExtra(":settings:fragment_args_key",
                        ComponentName(context, BridgeNotificationListener::class.java).flattenToString())
                })
            }) { Text("Show phone notifications on your PC") }
        } else {
            Text("Phone notifications are mirrored to your PC.", style = MaterialTheme.typography.bodyMedium)
        }

        Spacer(Modifier.height(24.dp))
        Text("Share a file to Bridge from any app to send it to your PC.", style = MaterialTheme.typography.bodyMedium)

        Spacer(Modifier.weight(1f))
        TextButton(onClick = { confirmForget = true }) { Text("Forget this PC") }
    }

    if (confirmForget) {
        AlertDialog(
            onDismissRequest = { confirmForget = false },
            title = { Text("Forget ${state.pcName ?: "this PC"}?") },
            text = { Text("You'll need to scan a new code on the PC to connect again.") },
            confirmButton = { TextButton(onClick = { confirmForget = false; onForget() }) { Text("Forget") } },
            dismissButton = { TextButton(onClick = { confirmForget = false }) { Text("Cancel") } },
        )
    }
}
