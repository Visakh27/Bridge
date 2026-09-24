package com.bridge.app.network

import java.io.DataInputStream
import java.io.DataOutputStream
import java.net.Socket
import java.nio.ByteBuffer
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
    private val output = DataOutputStream(socket.getOutputStream())
    private val writeLock = Mutex()

    val isConnected: Boolean get() = socket.isConnected && !socket.isClosed

    suspend fun sendFrame(data: ByteArray) = withContext(Dispatchers.IO) {
        writeLock.withLock {
            output.writeInt(data.size) // DataOutputStream.writeInt is big-endian, matching the .NET side
            output.write(data)
            output.flush()
        }
    }

    suspend fun receiveFrame(): ByteArray = withContext(Dispatchers.IO) {
        val len = input.readInt()
        require(len in 0..(64 * 1024 * 1024)) { "Refusing to read frame of size $len" }
        val buffer = ByteArray(len)
        input.readFully(buffer)
        buffer
    }

    fun close() {
        runCatching { socket.close() }
    }

    companion object {
        suspend fun connect(host: String, port: Int): TcpTransport = withContext(Dispatchers.IO) {
            val socket = Socket()
            socket.connect(java.net.InetSocketAddress(host, port), 10_000)
            TcpTransport(socket)
        }
    }
}
