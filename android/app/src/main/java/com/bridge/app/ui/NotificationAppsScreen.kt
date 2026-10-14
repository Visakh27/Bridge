package com.bridge.app.ui

import androidx.activity.compose.BackHandler
import androidx.compose.foundation.layout.*
import androidx.compose.foundation.lazy.LazyColumn
import androidx.compose.foundation.lazy.items
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.unit.dp
import com.bridge.app.core.BridgeApplication
import com.bridge.app.notifications.BridgeNotificationListener
import com.bridge.app.service.BridgeConnectionService

/**
 * Per-app notification mirroring: which apps' notifications appear on the PC.
 * Lists apps that have posted notifications since Bridge got access (and any
 * showing now). Turning an app off also removes its notifications from the PC.
 */
@Composable
fun NotificationAppsScreen(onBack: () -> Unit) {
    BackHandler(onBack = onBack)
    val context = LocalContext.current
    val mirror = (context.applicationContext as BridgeApplication).notificationMirror
    var saved by remember { mutableStateOf(mirror.filter.snapshot) }
    val apps = remember(saved.seen) {
        val showing = BridgeNotificationListener.instance?.all().orEmpty().associate { it.appId to it.appName }
        (saved.seen + showing).filterKeys { it != context.packageName }.toList().sortedBy { it.second.lowercase() }
    }

    Column(Modifier.fillMaxSize().padding(24.dp)) {
        TextButton(onClick = onBack) { Text("Back") }
        Text("Notifications on your PC", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(8.dp))
        Text("Choose which apps show their notifications on your PC. From the PC you can reply, press the app's " +
            "buttons, dismiss a notification, or stop an app's notifications; turn an app back on here.",
            style = MaterialTheme.typography.bodySmall)
        Spacer(Modifier.height(16.dp))
        Row(verticalAlignment = Alignment.CenterVertically) {
            Column(Modifier.weight(1f)) {
                Text("New apps")
                Text("Show notifications from apps not listed below", style = MaterialTheme.typography.bodySmall)
            }
            Switch(checked = saved.allowNewApps, onCheckedChange = {
                mirror.filter.setAllowNewApps(it)
                saved = mirror.filter.snapshot
            })
        }
        HorizontalDivider(Modifier.padding(vertical = 12.dp))
        if (apps.isEmpty()) {
            Text("Apps appear here once they post a notification.", style = MaterialTheme.typography.bodyMedium)
        }
        LazyColumn(Modifier.fillMaxWidth()) {
            items(apps, key = { it.first }) { (appId, name) ->
                Row(Modifier.fillMaxWidth().padding(vertical = 6.dp), verticalAlignment = Alignment.CenterVertically) {
                    Text(name, modifier = Modifier.weight(1f))
                    Switch(checked = saved.decisions[appId] ?: saved.allowNewApps, onCheckedChange = {
                        BridgeConnectionService.setAppMirrored(mirror, appId, it)
                        saved = mirror.filter.snapshot
                    })
                }
            }
        }
    }
}
