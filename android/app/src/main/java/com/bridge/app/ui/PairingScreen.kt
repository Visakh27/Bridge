package com.bridge.app.ui

import androidx.camera.core.CameraSelector
import androidx.camera.core.ImageAnalysis
import androidx.camera.core.Preview
import androidx.camera.lifecycle.ProcessCameraProvider
import androidx.camera.view.PreviewView
import androidx.compose.foundation.layout.*
import androidx.compose.material3.MaterialTheme
import androidx.compose.material3.Text
import androidx.compose.runtime.*
import androidx.compose.ui.Modifier
import androidx.compose.ui.platform.LocalContext
import androidx.compose.ui.platform.LocalLifecycleOwner
import androidx.compose.ui.unit.dp
import androidx.compose.ui.viewinterop.AndroidView
import androidx.core.content.ContextCompat
import com.bridge.app.network.PairingClient
import com.google.mlkit.vision.barcode.BarcodeScanning
import com.google.mlkit.vision.barcode.common.Barcode
import com.google.mlkit.vision.common.InputImage
import kotlinx.coroutines.launch
import java.util.concurrent.Executors

/**
 * Section 6 (pairing) / section 4.1 (first launch): live camera preview with
 * ML Kit barcode scanning for the Windows-displayed QR code. On a successful
 * scan it calls PairingClient.pairFromQrContent and, once paired, invokes
 * onPaired(host, port) so the caller can start BridgeConnectionService.
 */
@Composable
fun PairingScreen(pairingClient: PairingClient, deviceName: String, onPaired: (host: String, port: Int) -> Unit) {
    val lifecycleOwner = LocalLifecycleOwner.current
    val scope = rememberCoroutineScope()
    var statusText by remember { mutableStateOf("Point your camera at the QR code shown on your PC") }
    var handled by remember { mutableStateOf(false) }

    // Mutable holder so the ImageAnalysis.Analyzer callback (not itself composable)
    // can hand a scanned QR payload back into composition-scoped coroutine code.
    val onQrScanned: (String) -> Unit = { rawValue ->
        if (!handled) {
            handled = true
            scope.launch {
                statusText = "Pairing…"
                val result = pairingClient.pairFromQrContent(rawValue, deviceName)
                result.onSuccess { pairing ->
                    statusText = "Paired with ${pairing.device.deviceName}"
                    onPaired(pairing.host, pairing.port)
                }.onFailure { error ->
                    statusText = "Pairing failed: ${error.message}"
                    handled = false
                }
            }
        }
    }

    Column(Modifier.fillMaxSize().padding(16.dp)) {
        Text("Pair with your PC", style = MaterialTheme.typography.headlineSmall)
        Spacer(Modifier.height(8.dp))
        Text(statusText)
        Spacer(Modifier.height(16.dp))

        AndroidView(
            modifier = Modifier.fillMaxWidth().weight(1f),
            factory = { ctx ->
                val previewView = PreviewView(ctx)
                val cameraProviderFuture = ProcessCameraProvider.getInstance(ctx)
                val executor = Executors.newSingleThreadExecutor()
                val scanner = BarcodeScanning.getClient()

                cameraProviderFuture.addListener({
                    val cameraProvider = cameraProviderFuture.get()
                    val preview = Preview.Builder().build()
                        .also { it.setSurfaceProvider(previewView.surfaceProvider) }

                    val analysis = ImageAnalysis.Builder()
                        .setBackpressureStrategy(ImageAnalysis.STRATEGY_KEEP_ONLY_LATEST)
                        .build()

                    analysis.setAnalyzer(executor) { imageProxy ->
                        val mediaImage = imageProxy.image
                        if (mediaImage != null) {
                            val image = InputImage.fromMediaImage(mediaImage, imageProxy.imageInfo.rotationDegrees)
                            scanner.process(image)
                                .addOnSuccessListener { barcodes ->
                                    barcodes.firstOrNull { it.format == Barcode.FORMAT_QR_CODE }
                                        ?.rawValue?.let(onQrScanned)
                                }
                                .addOnCompleteListener { imageProxy.close() }
                        } else {
                            imageProxy.close()
                        }
                    }

                    cameraProvider.bindToLifecycle(
                        lifecycleOwner, CameraSelector.DEFAULT_BACK_CAMERA, preview, analysis
                    )
                }, ContextCompat.getMainExecutor(ctx))

                previewView
            }
        )
    }
}
