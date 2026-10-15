package com.bridge.app.ui

import android.content.Intent
import android.webkit.MimeTypeMap
import android.widget.Toast
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.text.style.TextOverflow
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import com.bridge.app.network.TransferDirection
import com.bridge.app.network.TransferEntry
import com.bridge.app.network.TransferHistory
import com.bridge.app.service.BridgeConnectionService
import kotlinx.coroutines.delay

/**
 * Section 4.2 (connected state): connection status (with why it isn't
 * connected and "Try now"), sending a folder, and recent transfers with
 * Retry / Open. Settings has the rest. Files are sent from other apps through
 * the share sheet (MainActivity).
 */
@Composable
fun DashboardScreen(state: BridgeConnectionService.State, onSettings: () -> Unit) {
    val context = LocalContext.current
    // Android's share sheet can't share folders, so folders go through the system folder picker.
    val pickFolder = rememberLauncherForActivityResult(ActivityResultContracts.OpenDocumentTree()) { uri ->
        if (uri == null) return@rememberLauncherForActivityResult
        val service = BridgeConnectionService.instance
        if (service == null) {
            Toast.makeText(context, "Bridge isn't connected to your PC right now.", Toast.LENGTH_LONG).show()
        } else {
            service.sendFolder(uri) { message ->
                ContextCompat.getMainExecutor(context).execute { Toast.makeText(context, message, Toast.LENGTH_LONG).show() }
            }
        }
    }

    Column(Modifier.fillMaxSize().padding(24.dp)) {
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Bridge", style = MaterialTheme.typography.headlineSmall, modifier = Modifier.weight(1f))
            TextButton(onClick = onSettings) { Text("Settings") }
        }
        Spacer(Modifier.height(8.dp))
        StatusCard(state)

        state.lastTransfer?.let {
            Spacer(Modifier.height(8.dp))
            Text(it, style = MaterialTheme.typography.bodyMedium)
        }
        Spacer(Modifier.height(16.dp))
        Text("Share a file to Bridge from any app to send it to your PC.", style = MaterialTheme.typography.bodyMedium)
        Spacer(Modifier.height(8.dp))
        OutlinedButton(
            enabled = state.status == BridgeConnectionService.Status.CONNECTED,
            onClick = { pickFolder.launch(null) },
        ) { Text("Send a folder") }

        Spacer(Modifier.height(20.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Text("Recent transfers", style = MaterialTheme.typography.titleMedium, modifier = Modifier.weight(1f))
            if (state.recent.isNotEmpty()) TextButton(onClick = { BridgeConnectionService.clearRecent() }) { Text("Clear") }
        }
        if (state.recent.isEmpty()) {
            Text("Files you send or receive appear here.", style = MaterialTheme.typography.bodySmall)
        }
        LazyColumn(Modifier.fillMaxWidth().weight(1f)) {
            items(state.recent, key = { it.transferId + it.direction }) { entry ->
                RecentRow(entry, connected = state.status == BridgeConnectionService.Status.CONNECTED)
            }
        }
    }
}

@Composable
private fun StatusCard(state: BridgeConnectionService.State) {
    val context = LocalContext.current
    val pc = state.pcName ?: "your PC"
    // Ticks once a second while a retry is scheduled, for the countdown.
    var now by remember { mutableLongStateOf(System.currentTimeMillis()) }
    LaunchedEffect(state.nextRetryAt) {
        while (state.nextRetryAt != null) { now = System.currentTimeMillis(); delay(1000) }
    }
    val title = when (state.status) {
        BridgeConnectionService.Status.CONNECTED -> "Connected to $pc"
        BridgeConnectionService.Status.CONNECTING -> if (state.nextRetryAt != null) "Not connected to $pc" else "Connecting to $pc…"
        else -> "Not connected"
    }
    Card(Modifier.fillMaxWidth()) {
        Column(Modifier.padding(16.dp)) {
            Text(title, style = MaterialTheme.typography.titleMedium)
            if (state.status != BridgeConnectionService.Status.CONNECTED) {
                state.lastError?.let { Text(it, style = MaterialTheme.typography.bodySmall) }
                val wait = state.nextRetryAt?.let { ((it - now) / 1000).coerceAtLeast(0) }
                Row(verticalAlignment = Alignment.CenterVertically) {
                    Text(
                        if (wait != null) "Trying again in $wait s" else "Make sure Bridge is open on the PC and both are on the same Wi-Fi.",
                        style = MaterialTheme.typography.bodySmall, modifier = Modifier.weight(1f),
                    )
                    TextButton(onClick = { BridgeConnectionService.tryNow(context) }) { Text("Try now") }
                }
            }
        }
    }
}

@Composable
private fun RecentRow(entry: TransferEntry, connected: Boolean) {
    val context = LocalContext.current
    Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f)) {
            val arrow = if (entry.direction == TransferDirection.SENDING) "↑" else "↓"
            val name = entry.folder?.let { "$it › ${entry.fileName}" } ?: entry.fileName
            Text("$arrow $name", maxLines = 1, overflow = TextOverflow.Ellipsis)
            Text(TransferHistory.describe(entry), style = MaterialTheme.typography.bodySmall)
        }
        if (entry.canRetry) {
            TextButton(enabled = connected, onClick = {
                if (!BridgeConnectionService.retry(entry))
                    Toast.makeText(context, "Couldn't retry: is Bridge connected to your PC?", Toast.LENGTH_LONG).show()
            }) { Text("Retry") }
        }
        if (entry.canOpen) {
            TextButton(onClick = {
                val file = entry.savedFile ?: return@TextButton
                runCatching {
                    val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
                    val type = MimeTypeMap.getSingleton().getMimeTypeFromExtension(file.extension.lowercase()) ?: "*/*"
                    context.startActivity(Intent(Intent.ACTION_VIEW).setDataAndType(uri, type).addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION or Intent.FLAG_ACTIVITY_NEW_TASK))
                }.onFailure { Toast.makeText(context, "No app here can open this file.", Toast.LENGTH_LONG).show() }
            }) { Text("Open") }
        }
    }
}
