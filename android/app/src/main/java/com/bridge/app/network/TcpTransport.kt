package com.bridge.app.network

import java.io.BufferedOutputStream
import java.io.DataInputStream
import java.io.DataOutputStream
import java.io.IOException
import java.net.InetSocketAddress
import java.net.Socket
import kotlinx.coroutines.Dispatchers
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

    val isConnected: Boolean get() = !closed && socket.isConnected && !socket.isClosed

    suspend fun sendFrame(data: ByteArray) = withContext(Dispatchers.IO) {
        writeLock.withLock {
            output.writeInt(data.size) // DataOutputStream.writeInt is big-endian, matching the .NET side
            output.write(data)
            output.flush()
        }
    }

    suspend fun receiveFrame(): ByteArray = withContext(Dispatchers.IO) {
        val len = input.readInt()
        if (len !in 0..MAX_FRAME_SIZE) throw IOException("Refusing to read frame of size $len")
        val buffer = ByteArray(len)
        input.readFully(buffer)
        buffer
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
