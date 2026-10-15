package com.bridge.app.ui

import android.Manifest
import android.content.ComponentName
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.provider.Settings
import android.widget.Toast
import androidx.activity.compose.BackHandler
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.rememberScrollState
import androidx.compose.foundation.verticalScroll
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.core.app.NotificationManagerCompat
import androidx.core.content.ContextCompat
import androidx.core.content.FileProvider
import androidx.lifecycle.Lifecycle
import androidx.lifecycle.LifecycleEventObserver
import com.bridge.app.core.BridgePrefs
import com.bridge.app.notifications.BridgeNotificationListener
import com.bridge.app.service.BridgeConnectionService

/** Settings: clipboard, notifications on the PC, diagnostics, about, forget the PC. */
@Composable
fun SettingsScreen(state: BridgeConnectionService.State, onBack: () -> Unit, onChooseApps: () -> Unit, onForget: () -> Unit) {
    BackHandler(onBack = onBack)
    val context = LocalContext.current
    var clipboard by remember { mutableStateOf(BridgeConnectionService.isClipboardSyncEnabled(context)) }
    var diagnostics by remember { mutableStateOf(BridgePrefs.diagnosticsEnabled(context)) }
    fun listenerAccess() = NotificationManagerCompat.getEnabledListenerPackages(context).contains(context.packageName)
    fun postAllowed() = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
    var access by remember { mutableStateOf(listenerAccess()) }
    var canPost by remember { mutableStateOf(postAllowed()) }
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, e -> if (e == Lifecycle.Event.ON_RESUME) { access = listenerAccess(); canPost = postAllowed() } }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    val askPost = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { canPost = it }
    var confirmForget by remember { mutableStateOf(false) }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp)) {
        TextButton(onClick = onBack) { Text("Back") }
        Text("Settings", style = MaterialTheme.typography.headlineSmall)

        Section("Clipboard")
        SwitchRow("Sync clipboard", "Copies from your phone are sent while Bridge is open (an Android privacy rule).", clipboard) {
            clipboard = it; BridgeConnectionService.setClipboardSyncEnabled(context, it)
        }

        Section("Notifications on your PC")
        if (!access) {
            Text("Let Bridge read notifications to show them on your PC, where you can reply, dismiss or use their buttons.",
                style = MaterialTheme.typography.bodyMedium)
            OutlinedButton(onClick = {
                context.startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS).apply {
                    putExtra(":settings:fragment_args_key", ComponentName(context, BridgeNotificationListener::class.java).flattenToString())
                })
            }) { Text("Allow notification access") }
        } else {
            Text("On. Turn it off in Android's notification access settings.", style = MaterialTheme.typography.bodyMedium)
            TextButton(onClick = onChooseApps) { Text("Choose apps") }
        }
        if (!canPost) {
            Spacer(Modifier.height(8.dp))
            Text("Bridge's own status notification is off, so you won't see when it's connected.", style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) askPost.launch(Manifest.permission.POST_NOTIFICATIONS) }) {
                Text("Allow Bridge notifications")
            }
        }

        Section("Diagnostics")
        SwitchRow("Keep a diagnostics log",
            "Records connection and transfer events (times, sizes, error types) to help fix problems. Never file names, messages, clipboard text or keys. It stays on this phone unless you share it.",
            diagnostics) { diagnostics = it; BridgePrefs.setDiagnosticsEnabled(context, it) }
        OutlinedButton(enabled = diagnostics, onClick = {
            val file = BridgeConnectionService.writeDiagnosticsReport(context)
            if (file == null) { Toast.makeText(context, "Couldn't create the report.", Toast.LENGTH_LONG).show(); return@OutlinedButton }
            val uri = FileProvider.getUriForFile(context, "${context.packageName}.files", file)
            val send = Intent(Intent.ACTION_SEND).apply {
                type = "text/plain"
                putExtra(Intent.EXTRA_STREAM, uri)
                putExtra(Intent.EXTRA_SUBJECT, "Bridge diagnostics report")
                addFlags(Intent.FLAG_GRANT_READ_URI_PERMISSION)
            }
            context.startActivity(Intent.createChooser(send, "Share diagnostics report"))
        }) { Text("Share diagnostics report") }

        Section("About")
        Text("Bridge ${BridgePrefs.appVersion(context)}", style = MaterialTheme.typography.bodyMedium)
        state.pcName?.let { Text("Paired with $it", style = MaterialTheme.typography.bodyMedium) }
        Spacer(Modifier.height(16.dp))
        TextButton(onClick = { confirmForget = true }) { Text("Forget this PC", color = MaterialTheme.colorScheme.error) }
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

@Composable
private fun Section(title: String) {
    Spacer(Modifier.height(20.dp))
    Text(title, style = MaterialTheme.typography.titleMedium)
    Spacer(Modifier.height(4.dp))
}

@Composable
private fun SwitchRow(title: String, detail: String, checked: Boolean, onChange: (Boolean) -> Unit) {
    Row(verticalAlignment = Alignment.CenterVertically) {
        Column(Modifier.weight(1f).padding(end = 12.dp)) {
            Text(title)
            Text(detail, style = MaterialTheme.typography.bodySmall)
        }
        Switch(checked = checked, onCheckedChange = onChange)
    }
}
