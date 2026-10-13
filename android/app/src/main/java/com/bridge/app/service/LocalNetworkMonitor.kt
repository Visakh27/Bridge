package com.bridge.app.service

import android.content.Context
import android.net.ConnectivityManager
import android.net.Network
import android.net.NetworkCapabilities
import com.bridge.app.network.DiscoveryWire
import java.net.Inet4Address
import java.net.InetAddress

/**
 * Android network glue for discovery and reconnects:
 *  - [broadcastTargets]: where to send discovery probes — the directed broadcast
 *    address of each local (Wi-Fi/Ethernet) IPv4 network, plus 255.255.255.255;
 *  - [start]: tells the caller when the default network appears (reconnect now)
 *    or is lost (drop the dead connection now).
 */
class LocalNetworkMonitor(context: Context) {
    private val connectivity = context.applicationContext.getSystemService(ConnectivityManager::class.java)
    private var callback: ConnectivityManager.NetworkCallback? = null

    fun broadcastTargets(): List<InetAddress> {
        val targets = mutableListOf<InetAddress>()
        for (network in connectivity.allNetworks) {
            val caps = connectivity.getNetworkCapabilities(network) ?: continue
            val local = caps.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) || caps.hasTransport(NetworkCapabilities.TRANSPORT_ETHERNET)
            if (!local) continue
            val props = connectivity.getLinkProperties(network) ?: continue
            for (link in props.linkAddresses) {
                val address = link.address as? Inet4Address ?: continue
                DiscoveryWire.directedBroadcast(address, link.prefixLength)?.let(targets::add)
            }
        }
        targets += InetAddress.getByName("255.255.255.255")
        return targets.distinct()
    }

    fun start(onAvailable: () -> Unit, onLost: () -> Unit) {
        stop()
        val cb = object : ConnectivityManager.NetworkCallback() {
            override fun onAvailable(network: Network) = onAvailable()
            override fun onLost(network: Network) = onLost()
        }
        runCatching { connectivity.registerDefaultNetworkCallback(cb) }.onSuccess { callback = cb }
    }

    fun stop() {
        callback?.let { runCatching { connectivity.unregisterNetworkCallback(it) } }
        callback = null
    }
}
