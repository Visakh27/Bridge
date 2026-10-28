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
 * Keeps the phone connected to one of its paired PCs (blueprint section 18):
 *  - tries the PCs in [chooser] order (the one the user chose, the one it was
 *    connected to last, then the rest; without a chooser, the newest pairing);
 *  - for each: connects to the stored address; if that fails, runs local
 *    discovery, and if the PC answers from a new address/port, saves it and
 *    connects there straight away (once per failure);
 *  - if the PC still can't be reached directly and it has a relay (remote access),
 *    tries the relay before moving on;
 *  - when every PC has failed, backs off (1 s … 30 s), but a network change
 *    ([wake]) retries now; when a session ends it starts over from the first PC;
 *  - a PC that says this phone is no longer paired is forgotten; the loop stops
 *    only when no paired PC is left.
 * One connection at a time. Platform-agnostic so it runs in JVM tests;
 * BridgeConnectionService owns it on a phone.
 */
class ReconnectLoop(
    private val store: PairedDeviceStore,
    private val discovery: PcDiscovery?,
    private val sessionFactory: (PairedDevice) -> SessionHandle,
    private val listener: Listener = object : Listener {},
    private val backoffMs: List<Long> = listOf(1_000L, 2_000L, 5_000L, 10_000L, 30_000L),
    /** A session that lasted this long was real, so the next failure starts the backoff over. */
    private val stableSessionMs: Long = 30_000,
    private val chooser: PcChooser? = null,
    /** A session to [PairedDevice] through its relay, or null if it has none. */
    private val relaySessionFactory: ((PairedDevice) -> SessionHandle?)? = null,
) {
    interface Listener {
        fun onConnecting(pc: PairedDevice) {}
        fun onAddressChanged(pc: PairedDevice) {}
        /** [pc] no longer recognises this phone and was forgotten; [remaining] PCs are still paired. */
        fun onUnpaired(pc: PairedDevice, remaining: Int = 0) {}
        /** A connection attempt failed (unreachable, timed out, wrong machine…) or a session ended with an error. */
        fun onAttemptFailed(pc: PairedDevice, error: Exception) {}
        /** The next attempt is in [delayMs] unless [wake] is called first ("Try now"). */
        fun onRetryScheduled(delayMs: Long) {}
        /** [pc] isn't reachable directly: trying its relay. */
        fun onTryingRelay(pc: PairedDevice) {}
    }

    enum class Outcome { NOT_PAIRED, UNPAIRED }

    private val wakeSignal = Channel<Unit>(Channel.CONFLATED)
    @Volatile private var current: SessionHandle? = null
    @Volatile private var switchRequested = false

    /** The PC being connected to or connected now (null between attempts). */
    @Volatile var currentPc: PairedDevice? = null
        private set

    /** Network became available: skip the remaining backoff and retry now. */
    fun wake() { wakeSignal.trySend(Unit) }

    /** Network was lost: drop the (now dead) connection instead of waiting for the watchdog. */
    fun dropCurrentSession() { current?.close() }

    /**
     * The user chose another PC (already pinned in the chooser): leave the current one and
     * start over from the top of the order, without waiting.
     */
    fun reconsider() {
        switchRequested = true
        current?.close()
        wake()
    }

    private suspend fun ordered(): List<PairedDevice> {
        val paired = store.list()
        return chooser?.order(paired) ?: listOfNotNull(paired.lastOrNull())
    }

    /** Runs until there is no pairing left or the coroutine is cancelled. */
    suspend fun run(): Outcome {
        var attempt = 0
        var unpairedAny = false
        while (currentCoroutineContext().isActive) {
            val candidates = ordered()
            if (candidates.isEmpty()) return if (unpairedAny) Outcome.UNPAIRED else Outcome.NOT_PAIRED
            switchRequested = false
            var sessionRan = false
            for (candidate in candidates) {
                if (switchRequested) break
                var pc = candidate
                var triedNewAddress = false
                while (true) { // at most one immediate retry, after discovery finds a new address
                    listener.onConnecting(pc)
                    currentPc = pc
                    val startedAt = System.currentTimeMillis()
                    var retryNow = false
                    var unreachable = false
                    try {
                        runOnce(sessionFactory(pc))
                        sessionRan = true
                    } catch (e: CancellationException) {
                        throw e
                    } catch (e: DeviceUnpairedException) {
                        store.revoke(pc.deviceId)
                        chooser?.forgotten(pc.deviceId)
                        unpairedAny = true
                        listener.onUnpaired(pc, store.list().size)
                    } catch (e: Exception) {
                        unreachable = e is IOException || e is AuthenticationException
                        listener.onAttemptFailed(pc, e)
                        // Unreachable / timed out (IOException) or someone else answered at that
                        // address (AuthenticationException): the PC may have moved. Look for it,
                        // once per failure, so a PC answering from two addresses that both fail
                        // can't make us bounce between them with no backoff.
                        if ((e is IOException || e is AuthenticationException) && !triedNewAddress && !switchRequested) {
                            val found = runCatching { discovery?.find(pc) }.getOrNull()
                            if (found != null && (found.host != pc.host || found.port != pc.port)) {
                                pc = pc.copy(host = found.host, port = found.port)
                                store.save(pc)
                                listener.onAddressChanged(pc)
                                retryNow = true
                                triedNewAddress = true
                            }
                        }
                    } finally {
                        currentPc = null
                    }
                    // Not reachable directly (away from home?): its relay, if it has one.
                    if (!retryNow && unreachable && !switchRequested) {
                        val relayed = relaySessionFactory?.invoke(pc)
                        if (relayed != null) {
                            listener.onTryingRelay(pc)
                            currentPc = pc
                            try {
                                runOnce(relayed)
                                sessionRan = true
                            } catch (e: CancellationException) {
                                throw e
                            } catch (e: DeviceUnpairedException) {
                                store.revoke(pc.deviceId)
                                chooser?.forgotten(pc.deviceId)
                                unpairedAny = true
                                listener.onUnpaired(pc, store.list().size)
                            } catch (e: Exception) {
                                listener.onAttemptFailed(pc, e)
                            } finally {
                                currentPc = null
                            }
                        }
                    }
                    if (System.currentTimeMillis() - startedAt >= stableSessionMs) attempt = 0
                    if (!retryNow) break
                }
                if (sessionRan) break // it ended: start over from the first PC (after the usual pause)
            }
            if (store.list().isEmpty()) return Outcome.UNPAIRED.takeIf { unpairedAny } ?: Outcome.NOT_PAIRED
            if (switchRequested) { switchRequested = false; attempt = 0; continue } // the user picked another PC: go now
            val wait = backoffMs[minOf(attempt, backoffMs.size - 1)]
            attempt++
            listener.onRetryScheduled(wait)
            withTimeoutOrNull(wait) { wakeSignal.receive() } // a network change cuts the wait short
        }
        throw CancellationException()
    }

    private suspend fun runOnce(session: SessionHandle) {
        current = session
        try {
            session.run()
        } finally {
            current = null
        }
    }
}
