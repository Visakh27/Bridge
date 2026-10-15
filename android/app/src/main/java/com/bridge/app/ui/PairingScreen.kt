package com.bridge.app.ui

import android.Manifest
import android.content.pm.PackageManager
import androidx.activity.compose.rememberLauncherForActivityResult
import androidx.activity.result.contract.ActivityResultContracts
import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.*
import androidx.compose.material3.Button
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.bridge.app.core.BridgeApplication
import com.bridge.app.core.LogField
import com.bridge.app.network.PairingClient
import com.google.mlkit.vision.barcode.BarcodeScannerOptions
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import kotlinx.coroutines.launch
import java.util.concurrent.Executors

/**
 * Section 6 (pairing) / section 4.1 (first launch): asks for the camera,
 * shows a live preview with ML Kit QR scanning for the code on the PC, and on
 * a successful scan runs PairingClient. The camera is released as soon as
 * this screen leaves composition.
 */
@Composable
fun PairingScreen(
    pairingClient: PairingClient,
    deviceName: String,
    notice: String? = null,
    onPaired: () -> Unit,
) {
    val context = LocalContext.current
    val scope = rememberCoroutineScope()
    var statusText by remember { mutableStateOf("Point your camera at the QR code shown on your PC") }
    var handled by remember { mutableStateOf(false) }
    var hasCamera by remember {
        mutableStateOf(ContextCompat.checkSelfPermission(context, Manifest.permission.CAMERA) == PackageManager.PERMISSION_GRANTED)
    }
    val requestCamera = rememberLauncherForActivityResult(ActivityResultContracts.RequestPermission()) { granted ->
        hasCamera = granted
    }
    LaunchedEffect(Unit) { if (!hasCamera) requestCamera.launch(Manifest.permission.CAMERA) }

    val onQrScanned: (String) -> Unit = { rawValue ->
        if (!handled) {
            handled = true
            scope.launch {
                statusText = "Pairing…"
                pairingClient.pairFromQrContent(rawValue, deviceName)
                    .onSuccess { pairing ->
                        val log = (context.applicationContext as BridgeApplication).diagnostics
                        log.info("pair.succeeded", LogField.id("peer", pairing.device.deviceId, log))
                        statusText = "Paired with ${pairing.device.deviceName}"
                        onPaired()
                    }
                    .onFailure { error ->
                        (context.applicationContext as BridgeApplication).diagnostics.warn("pair.failed", LogField.error(error))
                        statusText = "Pairing failed: ${error.message ?: "couldn't reach the PC. Are both devices on the same Wi-Fi?"}"
                        handled = false
                    }
            }
        }
    }

    Column(Modifier.fillMaxSize().padding(16.dp)) {
        Text("Pair with your PC", style = MaterialTheme.typography.headlineSmall)
        if (notice != null) {
            Spacer(Modifier.height(8.dp))
            Text(notice, color = MaterialTheme.colorScheme.error)
        }
        Spacer(Modifier.height(8.dp))
        Text(statusText)
        Spacer(Modifier.height(16.dp))

        if (hasCamera) {
            QrScanner(Modifier.fillMaxWidth().weight(1f), onQrScanned)
        } else {
            Text("Bridge needs the camera to scan the pairing code on your PC.")
            Spacer(Modifier.height(8.dp))
            Button(onClick = { requestCamera.launch(Manifest.permission.CAMERA) }) { Text("Allow camera") }
        }
    }
}

@Composable
private fun QrScanner(modifier: Modifier, onQrScanned: (String) -> Unit) {
    val context = LocalContext.current
    val lifecycleOwner = LocalLifecycleOwner.current
    val latestOnScanned by rememberUpdatedState(onQrScanned)
    val executor = remember { Executors.newSingleThreadExecutor() }
    val scanner = remember {
        BarcodeScanning.getClient(BarcodeScannerOptions.Builder().setBarcodeFormats(Barcode.FORMAT_QR_CODE).build())
    }
    val providerFuture = remember { ProcessCameraProvider.getInstance(context) }
    val disposed = remember { java.util.concurrent.atomic.AtomicBoolean(false) }

    DisposableEffect(lifecycleOwner) {
        onDispose {
            // Without this the camera stayed bound to the activity after pairing.
            disposed.set(true)
            runCatching { if (providerFuture.isDone) providerFuture.get().unbindAll() }
            scanner.close()
            executor.shutdown()
        }
    }

    AndroidView(
        modifier = modifier,
        factory = { ctx ->
            val previewView = PreviewView(ctx)
            providerFuture.addListener({
                if (disposed.get()) return@addListener // left the screen before the camera was ready
                val cameraProvider = providerFuture.get()
                val preview = Preview.Builder().build().also { it.setSurfaceProvider(previewView.surfaceProvider) }
                val analysis = ImageAnalysis.Builder()
                    .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                    .build()

                analysis.setAnalyzer(executor) { imageProxy ->
                    val mediaImage = imageProxy.image
                    if (mediaImage != null) {
                        val image = InputImage.fromMediaImage(mediaImage, imageProxy.imageInfo.rotationDegrees)
                        scanner.process(image)
                            .addOnSuccessListener { barcodes ->
                                barcodes.firstOrNull { it.format == Barcode.FORMAT_QR_CODE }?.rawValue?.let { latestOnScanned(it) }
                            }
                            .addOnCompleteListener { imageProxy.close() }
                    } else {
                        imageProxy.close()
                    }
                }

                runCatching {
                    cameraProvider.unbindAll()
                    cameraProvider.bindToLifecycle(lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis)
                }
            }, ContextCompat.getMainExecutor(ctx))
            previewView
        },
    )
}
