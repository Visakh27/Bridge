package com.bridge.app.network

import java.io.IOException
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.channels.Channel
import kotlinx.coroutines.currentCoroutineContext
import kotlinx.coroutines.isActive
import kotlinx.coroutines.withTimeoutOrNull

/** What [ReconnectLoop] drives: one connection attempt that runs until it ends. */
interface SessionHandle {
    /** Runs until the connection ends (returns) or fails to start (throws). */
    suspend fun run()
    fun close()
}

/**
 * Keeps the phone connected to its paired PC (blueprint section 18):
 *  - connects to the stored address;
 *  - if that fails, runs local discovery, and if the PC answers from a new
 *    address/port, saves it and connects there straight away (once per
 *    failure; after that the normal backoff applies);
 *  - otherwise backs off (1 s … 30 s), but a network change ([wake]) retries now;
 *  - stops if the PC says this phone is no longer paired.
 * Platform-agnostic so it runs in JVM tests; BridgeConnectionService owns it on a phone.
 */
class ReconnectLoop(
    private val store: PairedDeviceStore,
    private val discovery: PcDiscovery?,
    private val sessionFactory: (PairedDevice) -> SessionHandle,
    private val listener: Listener = object : Listener {},
    private val backoffMs: List<Long> = listOf(1_000L, 2_000L, 5_000L, 10_000L, 30_000L),
    /** A session that lasted this long was real, so the next failure starts the backoff over. */
    private val stableSessionMs: Long = 30_000,
) {
    interface Listener {
        fun onConnecting(pc: PairedDevice) {}
        fun onAddressChanged(pc: PairedDevice) {}
        fun onUnpaired(pc: PairedDevice) {}
        /** A connection attempt failed (unreachable, timed out, wrong machine…) or a session ended with an error. */
        fun onAttemptFailed(pc: PairedDevice, error: Exception) {}
        /** The next attempt is in [delayMs] unless [wake] is called first ("Try now"). */
        fun onRetryScheduled(delayMs: Long) {}
    }

    enum class Outcome { NOT_PAIRED, UNPAIRED }

    private val wakeSignal = Channel<Unit>(Channel.CONFLATED)
    @Volatile private var current: SessionHandle? = null

    /** Network became available: skip the remaining backoff and retry now. */
    fun wake() { wakeSignal.trySend(Unit) }

    /** Network was lost: drop the (now dead) connection instead of waiting for the watchdog. */
    fun dropCurrentSession() { current?.close() }

    /** Runs until there is no pairing left or the coroutine is cancelled. */
    suspend fun run(): Outcome {
        var attempt = 0
        var lastWasImmediate = false
        while (currentCoroutineContext().isActive) {
            var pc = store.list().lastOrNull() ?: return Outcome.NOT_PAIRED
            listener.onConnecting(pc)

            var retryNow = false
            val startedAt = System.currentTimeMillis()
            try {
                runOnce(pc)
            } catch (e: CancellationException) {
                throw e
            } catch (e: DeviceUnpairedException) {
                store.revoke(pc.deviceId)
                listener.onUnpaired(pc)
                return Outcome.UNPAIRED
            } catch (e: Exception) {
                listener.onAttemptFailed(pc, e)
                // Unreachable / timed out (IOException) or someone else answered at that
                // address (AuthenticationException): the PC may have moved. Look for it.
                // At most one immediate retry per failure: a PC answering from two
                // addresses that both fail must not make us bounce between them
                // with no backoff.
                if ((e is IOException || e is AuthenticationException) && !lastWasImmediate) {
                    val found = runCatching { discovery?.find(pc) }.getOrNull()
                    if (found != null && (found.host != pc.host || found.port != pc.port)) {
                        pc = pc.copy(host = found.host, port = found.port)
                        store.save(pc)
                        listener.onAddressChanged(pc)
                        retryNow = true
                    }
                }
            }

            if (System.currentTimeMillis() - startedAt >= stableSessionMs) attempt = 0
            lastWasImmediate = retryNow
            if (retryNow) continue
            val wait = backoffMs[minOf(attempt, backoffMs.size - 1)]
            attempt++
            listener.onRetryScheduled(wait)
            withTimeoutOrNull(wait) { wakeSignal.receive() } // a network change cuts the wait short
        }
        throw CancellationException()
    }

    private suspend fun runOnce(pc: PairedDevice) {
        val session = sessionFactory(pc)
        current = session
        try {
            session.run()
        } finally {
            current = null
        }
    }
}
