package com.bridge.app.network

import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import kotlinx.coroutines.CancellationException
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.async
import kotlinx.coroutines.sync.Mutex
import kotlinx.coroutines.sync.withLock
import kotlinx.coroutines.withContext

/**
 * Length-prefixed TCP framing, wire compatible with Bridge.Network.TcpTransport
 * on Windows: [4-byte big-endian length][payload].
 */
class TcpTransport(private val socket: Socket) {
    private val input = DataInputStream(socket.getInputStream())
    private val output = DataOutputStream(BufferedOutputStream(socket.getOutputStream()))
    private val writeLock = Mutex()
    @Volatile private var closed = false
    private val ioScope = CoroutineScope(SupervisorJob() + Dispatchers.IO)

    val isConnected: Boolean get() = !closed && socket.isConnected && !socket.isClosed

    suspend fun sendFrame(data: ByteArray) {
        writeLock.withLock {
            blockingIo {
                output.writeInt(data.size) // DataOutputStream.writeInt is big-endian, matching the .NET side
                output.write(data)
                output.flush()
            }
        }
    }

    suspend fun receiveFrame(): ByteArray = blockingIo {
        val len = input.readInt()
        if (len !in 0..MAX_FRAME_SIZE) throw IOException("Refusing to read frame of size $len")
        val buffer = ByteArray(len)
        input.readFully(buffer)
        buffer
    }

    /**
     * Runs a blocking socket call on the IO dispatcher. java.net.Socket ignores
     * coroutine cancellation, so a cancelled caller (e.g. withTimeout) closes the
     * socket to unblock it — otherwise a silent peer would hang us forever.
     */
    private suspend fun <T> blockingIo(block: () -> T): T {
        // Not a child of the caller: when the caller is cancelled we want *its*
        // CancellationException (e.g. a timeout) to surface, not the IOException
        // the unblocked socket call throws after close().
        val op = ioScope.async { block() }
        try {
            return op.await()
        } catch (e: CancellationException) {
            close()
            throw e
        }
    }

    fun close() {
        closed = true
        runCatching { socket.close() }
    }

    companion object {
        const val MAX_FRAME_SIZE = 16 * 1024 * 1024
        private const val CONNECT_TIMEOUT_MS = 10_000

        suspend fun connect(host: String, port: Int): TcpTransport = withContext(Dispatchers.IO) {
            val socket = Socket()
            try {
                socket.tcpNoDelay = true
                socket.connect(InetSocketAddress(host, port), CONNECT_TIMEOUT_MS)
                TcpTransport(socket)
            } catch (e: Exception) {
                runCatching { socket.close() }
                throw e
            }
        }
    }
}
