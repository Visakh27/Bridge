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
import com.bridge.app.network.PairedDevice
import kotlinx.coroutines.launch
import com.bridge.app.notifications.BridgeNotificationListener
import com.bridge.app.photos.MediaStorePhotoLibrary
import com.bridge.app.service.BridgeConnectionService

/** Settings: your PCs, clipboard, notifications on the PC, links, photos, diagnostics, about. */
@Composable
fun SettingsScreen(
    state: BridgeConnectionService.State,
    onBack: () -> Unit,
    onChooseApps: () -> Unit,
    /** A PC was forgotten; [left] PCs are still paired. */
    onForgotten: (left: Int) -> Unit,
    onPairAnother: () -> Unit,
) {
    BackHandler(onBack = onBack)
    val context = LocalContext.current
    var clipboard by remember { mutableStateOf(BridgeConnectionService.isClipboardSyncEnabled(context)) }
    var diagnostics by remember { mutableStateOf(BridgePrefs.diagnosticsEnabled(context)) }
    fun listenerAccess() = NotificationManagerCompat.getEnabledListenerPackages(context).contains(context.packageName)
    fun postAllowed() = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
    var access by remember { mutableStateOf(listenerAccess()) }
    var canPost by remember { mutableStateOf(postAllowed()) }
    var photos by remember { mutableStateOf(BridgePrefs.photosEnabled(context)) }
    var handoff by remember { mutableStateOf(BridgePrefs.handoffEnabled(context)) }
    var photoAccess by remember { mutableStateOf(MediaStorePhotoLibrary.hasPermission(context)) }
    val lifecycleOwner = LocalLifecycleOwner.current
    DisposableEffect(lifecycleOwner) {
        val observer = LifecycleEventObserver { _, e ->
            if (e == Lifecycle.Event.ON_RESUME) { access = listenerAccess(); canPost = postAllowed(); photoAccess = MediaStorePhotoLibrary.hasPermission(context) }
        }
        lifecycleOwner.lifecycle.addObserver(observer)
        onDispose { lifecycleOwner.lifecycle.removeObserver(observer) }
    }
    val askPost = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { canPost = it }
    val askPhotos = rememberLauncherForActivityResult(ActivityResultContracts.RequestMultiplePermissions()) {
        photoAccess = MediaStorePhotoLibrary.hasPermission(context)
        BridgeConnectionService.photosSettingChanged(context)
    }
    var confirmForget by remember { mutableStateOf<PairedDevice?>(null) }
    var pcs by remember { mutableStateOf<List<PairedDevice>>(emptyList()) }
    var pinned by remember { mutableStateOf(BridgeConnectionService.pinnedPcId(context)) }
    var refresh by remember { mutableStateOf(0) }
    val scope = rememberCoroutineScope()
    LaunchedEffect(state.pcId, state.status, refresh) {
        pcs = BridgeConnectionService.pairedPcs(context)
        pinned = BridgeConnectionService.pinnedPcId(context)
    }

    Column(Modifier.fillMaxSize().verticalScroll(rememberScrollState()).padding(24.dp)) {
        TextButton(onClick = onBack) { Text("Back") }
        Text("Settings", style = MaterialTheme.typography.headlineSmall)

        Section("Your PCs")
        Text("This phone connects to one PC at a time: the one you choose here, or else the one it used last, then any other it can reach.",
            style = MaterialTheme.typography.bodySmall)
        for (pc in pcs) {
            val connectedHere = state.pcId == pc.deviceId && state.status == BridgeConnectionService.Status.CONNECTED
            Row(verticalAlignment = Alignment.CenterVertically, modifier = Modifier.padding(top = 8.dp)) {
                Column(Modifier.weight(1f).padding(end = 8.dp)) {
                    Text(pc.deviceName)
                    Text(
                        when {
                            connectedHere -> "Connected"
                            state.pcId == pc.deviceId && state.status == BridgeConnectionService.Status.CONNECTING -> "Connecting…"
                            pinned == pc.deviceId -> "Your choice"
                            else -> "Not connected"
                        } + when {
                            connectedHere && state.viaRelay -> " · through your relay"
                            BridgeConnectionService.hasRelay(context, pc.deviceId) -> " · reachable when away"
                            else -> ""
                        },
                        style = MaterialTheme.typography.bodySmall,
                    )
                }
                if (!connectedHere) TextButton(onClick = {
                    BridgeConnectionService.switchToPc(context, pc.deviceId)
                    pinned = pc.deviceId
                    refresh++
                }) { Text("Use this PC") }
                TextButton(onClick = { confirmForget = pc }) { Text("Forget", color = MaterialTheme.colorScheme.error) }
            }
        }
        OutlinedButton(onClick = onPairAnother, modifier = Modifier.padding(top = 8.dp)) { Text("Pair another PC") }

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

        Section("Links and text")
        SwitchRow("Receive links and text from your PC",
            "A link from your PC shows a notification you can tap to open it; text goes on this phone's clipboard. To send one to your PC, share it to Bridge from any app.",
            handoff) { handoff = it; BridgePrefs.setHandoffEnabled(context, it) }

        Section("Photos on your PC")
        SwitchRow("Let your PC browse photos",
            "Your PC can show this phone's photos and save copies. It sees only what Android lets Bridge see, and only while this is on.",
            photos) {
            photos = it
            BridgePrefs.setPhotosEnabled(context, it)
            BridgeConnectionService.photosSettingChanged(context)
            if (it && !photoAccess) askPhotos.launch(MediaStorePhotoLibrary.permissions())
        }
        if (photos && !photoAccess) {
            Text("Bridge doesn't have access to your photos yet.", style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { askPhotos.launch(MediaStorePhotoLibrary.permissions()) }) { Text("Allow photo access") }
        } else if (photos && Build.VERSION.SDK_INT >= 34 &&
            ContextCompat.checkSelfPermission(context, Manifest.permission.READ_MEDIA_IMAGES) != PackageManager.PERMISSION_GRANTED) {
            // Android 14 "selected photos" access: the PC sees just those.
            Text("Your PC sees only the photos you selected.", style = MaterialTheme.typography.bodySmall)
            TextButton(onClick = { askPhotos.launch(MediaStorePhotoLibrary.permissions()) }) { Text("Change selection") }
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
        Spacer(Modifier.height(16.dp))
    }

    confirmForget?.let { pc ->
        AlertDialog(
            onDismissRequest = { confirmForget = null },
            title = { Text("Forget ${pc.deviceName}?") },
            text = { Text("You'll need to scan a new code on that PC to connect to it again.") },
            confirmButton = {
                TextButton(onClick = {
                    confirmForget = null
                    scope.launch {
                        val left = BridgeConnectionService.forgetPc(context, pc.deviceId)
                        refresh++
                        onForgotten(left)
                    }
                }) { Text("Forget") }
            },
            dismissButton = { TextButton(onClick = { confirmForget = null }) { Text("Cancel") } },
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
