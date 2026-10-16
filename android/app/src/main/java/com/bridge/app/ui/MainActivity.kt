package com.bridge.app.ui

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.OpenableColumns
import android.widget.Toast
import androidx.activity.ComponentActivity
import androidx.activity.compose.setContent
import androidx.activity.result.contract.ActivityResultContracts
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Surface
import androidx.compose.runtime.*
import androidx.core.content.ContextCompat
import androidx.core.content.IntentCompat
import androidx.lifecycle.lifecycleScope
import androidx.compose.runtime.saveable.rememberSaveable
import com.bridge.app.core.BridgeApplication
import com.bridge.app.core.BridgePrefs
import com.bridge.app.network.PairingClient
import com.bridge.app.network.SafeFileName
import com.bridge.app.service.BridgeConnectionService
import com.bridge.app.service.SharedPrefsPairedDeviceStore
import java.io.File
import java.util.UUID
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.launch
import kotlinx.coroutines.withContext

class MainActivity : ComponentActivity() {
    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)

        val identity = (application as BridgeApplication).identity
        val store = SharedPrefsPairedDeviceStore(this)
        val pairingClient = PairingClient(identity, store)

        // The notification permission is asked in the first-run guide (OnboardingScreen) or Settings, with its reason.

        setContent {
            MaterialTheme {
                Surface {
                    var paired by remember { mutableStateOf<Boolean?>(null) } // null = still loading
                    var onboarded by remember { mutableStateOf(BridgePrefs.onboardingDone(this@MainActivity)) }
                    var screen by rememberSaveable { mutableStateOf("dashboard") }
                    val connection by BridgeConnectionService.state.collectAsState()

                    LaunchedEffect(Unit) {
                        paired = store.current() != null
                        if (paired == true) {
                            BridgePrefs.setOnboardingDone(this@MainActivity) // upgraded from 0.1: already set up
                            onboarded = true
                            BridgeConnectionService.start(this@MainActivity)
                        }
                    }
                    LaunchedEffect(connection.status) {
                        if (connection.status == BridgeConnectionService.Status.UNPAIRED) paired = false
                    }

                    when {
                        paired == null -> Unit
                        // First run (an already-paired install upgrading from 0.1 skips the guide).
                        paired == false && !onboarded -> OnboardingScreen {
                            BridgePrefs.setOnboardingDone(this@MainActivity)
                            onboarded = true
                        }
                        paired == false -> PairingScreen(
                            pairingClient = pairingClient,
                            deviceName = Build.MODEL,
                            notice = if (connection.status == BridgeConnectionService.Status.UNPAIRED)
                                "${connection.pcName ?: "Your PC"} no longer recognises this phone. Scan a new code to pair again." else null,
                        ) {
                            paired = true
                            screen = "dashboard"
                            BridgeConnectionService.start(this@MainActivity)
                        }
                        screen == "settings" -> SettingsScreen(
                            state = connection,
                            onBack = { screen = "dashboard" },
                            onChooseApps = { screen = "apps" },
                            onForget = {
                                lifecycleScope.launch {
                                    BridgeConnectionService.forgetPc(this@MainActivity)
                                    paired = false
                                }
                            },
                        )
                        screen == "apps" -> NotificationAppsScreen(onBack = { screen = "settings" })
                        else -> DashboardScreen(state = connection, onSettings = { screen = "settings" })
                    }
                }
            }
        }

        handleShareIntent(intent)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        handleShareIntent(intent)
    }

    /** Android → Windows share target (manifest SEND / SEND_MULTIPLE intent filters). */
    private fun handleShareIntent(intent: Intent?) {
        val uris: List<Uri> = when (intent?.action) {
            Intent.ACTION_SEND -> listOfNotNull(IntentCompat.getParcelableExtra(intent, Intent.EXTRA_STREAM, Uri::class.java))
            Intent.ACTION_SEND_MULTIPLE -> IntentCompat.getParcelableArrayListExtra(intent, Intent.EXTRA_STREAM, Uri::class.java).orEmpty()
            else -> return
        }
        setIntent(Intent(this, MainActivity::class.java)) // don't re-send on rotation
        if (uris.isEmpty()) {
            Toast.makeText(this, "Bridge can send files to your PC; that share didn't include a file.", Toast.LENGTH_LONG).show()
            return
        }

        lifecycleScope.launch {
            var sent = 0
            for (uri in uris) {
                val (copy, name) = withContext(Dispatchers.IO) { copyToCache(uri) } ?: continue
                val service = BridgeConnectionService.instance
                if (service != null && service.sendFile(copy, name, deleteAfter = true)) sent++ else copy.delete()
            }
            val message = when {
                sent == uris.size -> if (sent == 1) "Sending to your PC…" else "Sending $sent files to your PC…"
                sent == 0 -> "Bridge isn't connected to your PC right now."
                else -> "Sending $sent of ${uris.size} files; the rest couldn't be read."
            }
            Toast.makeText(this@MainActivity, message, Toast.LENGTH_LONG).show()
        }
    }

    /** Content URIs are only readable while we hold the grant, so copy to a private temp file first. */
    private fun copyToCache(uri: Uri): Pair<File, String>? = runCatching {
        val name = contentResolver.query(uri, arrayOf(OpenableColumns.DISPLAY_NAME), null, null, null)?.use { c ->
            if (c.moveToFirst()) c.getString(0) else null
        } ?: uri.lastPathSegment ?: "shared_file"
        val dir = File(cacheDir, "share").apply { mkdirs() }
        val copy = File(dir, UUID.randomUUID().toString())
        contentResolver.openInputStream(uri)!!.use { input -> copy.outputStream().use { input.copyTo(it) } }
        copy to SafeFileName.sanitize(name)
    }.getOrNull()
}
