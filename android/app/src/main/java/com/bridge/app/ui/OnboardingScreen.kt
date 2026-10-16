package com.bridge.app.ui

import android.Manifest
import android.content.pm.PackageManager
import android.os.Build
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.foundation.Image
import androidx.compose.foundation.layout.*
import androidx.compose.material3.*
import androidx.compose.runtime.*
import androidx.compose.runtime.saveable.rememberSaveable
import androidx.compose.ui.Alignment
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.res.painterResource
import androidx.compose.ui.unit.dp
import androidx.core.content.ContextCompat
import com.bridge.app.R

/**
 * First launch (blueprint section 4.1): what Bridge does, what it needs, and
 * a hand-off to pairing. The notification permission (Android 13+, for the
 * small "connected" status notification) is asked here, with the reason,
 * instead of at app start.
 */
@Composable
fun OnboardingScreen(onDone: () -> Unit) {
    val context = LocalContext.current
    var page by rememberSaveable { mutableIntStateOf(0) }
    fun notificationsAllowed() = Build.VERSION.SDK_INT < Build.VERSION_CODES.TIRAMISU ||
        ContextCompat.checkSelfPermission(context, Manifest.permission.POST_NOTIFICATIONS) == PackageManager.PERMISSION_GRANTED
    val askNotifications = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { page = 2 }

    Column(Modifier.fillMaxSize().padding(24.dp)) {
        Text("Step ${page + 1} of 3", style = MaterialTheme.typography.labelMedium)
        Spacer(Modifier.height(24.dp))
        when (page) {
            0 -> {
                Image(painterResource(R.drawable.ic_bridge_logo), contentDescription = null, modifier = Modifier.size(72.dp))
                Spacer(Modifier.height(16.dp))
                Text("Welcome to Bridge", style = MaterialTheme.typography.headlineMedium)
                Spacer(Modifier.height(12.dp))
                Text("Send files and folders between this phone and your Windows PC, share the clipboard, and see and answer " +
                    "your notifications on the PC. Everything goes over your own Wi-Fi, never through the internet.",
                    style = MaterialTheme.typography.bodyLarge)
            }
            1 -> {
                Text("Before you pair", style = MaterialTheme.typography.headlineMedium)
                Spacer(Modifier.height(12.dp))
                Bullet("Install Bridge on your PC (Windows 10 or 11) and open it. It shows a code to scan.")
                Bullet("Keep this phone and the PC on the same Wi-Fi network.")
                Bullet("Bridge stays connected in the background and shows a small status notification while it does.")
            }
            else -> {
                Text("Scan the code", style = MaterialTheme.typography.headlineMedium)
                Spacer(Modifier.height(12.dp))
                Text("Next, point the camera at the code on your PC. Bridge uses the camera only for this.",
                    style = MaterialTheme.typography.bodyLarge)
            }
        }
        Spacer(Modifier.weight(1f))
        Row(Modifier.fillMaxWidth(), horizontalArrangement = Arrangement.SpaceBetween, verticalAlignment = Alignment.CenterVertically) {
            if (page > 0) TextButton(onClick = { page-- }) { Text("Back") } else Spacer(Modifier.width(1.dp))
            when {
                page == 1 && !notificationsAllowed() -> Row {
                    TextButton(onClick = { page = 2 }) { Text("Not now") }
                    Button(onClick = { askNotifications.launch(Manifest.permission.POST_NOTIFICATIONS) }) { Text("Allow notifications") }
                }
                page < 2 -> Button(onClick = { page++ }) { Text("Next") }
                else -> Button(onClick = onDone) { Text("Scan the code") }
            }
        }
    }
}

@Composable
private fun Bullet(text: String) {
    Row(Modifier.padding(vertical = 6.dp)) {
        Text("•", modifier = Modifier.padding(end = 8.dp), style = MaterialTheme.typography.bodyLarge)
        Text(text, style = MaterialTheme.typography.bodyLarge)
    }
}
