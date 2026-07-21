package com.jodu.app.network

import com.jodu.app.protocol.DiscoveryPayload
import com.jodu.app.protocol.EventTypes
import com.jodu.app.protocol.JoduJson
import com.jodu.app.protocol.JoduPorts
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.NetworkInterface

class DiscoveryService(
    private val scope: CoroutineScope,
    private val deviceId: String,
    private val deviceName: String,
    private val onDesktopFound: (DiscoveryPayload) -> Unit,
) {
    private var socket: DatagramSocket? = null
    private var listenJob: Job? = null
    private var announceJob: Job? = null

    fun start() {
        if (socket != null) return
        val s = DatagramSocket(null).apply {
            reuseAddress = true
            broadcast = true
            bind(java.net.InetSocketAddress(JoduPorts.DISCOVERY))
        }
        socket = s

        listenJob = scope.launch(Dispatchers.IO) {
            val buffer = ByteArray(4096)
            while (isActive) {
                try {
                    val packet = DatagramPacket(buffer, buffer.size)
                    s.receive(packet)
                    val raw = String(packet.data, 0, packet.length, Charsets.UTF_8)
                    val msg = runCatching { JoduJson.parse(raw) }.getOrNull() ?: continue
                    if (msg.type != EventTypes.DISCOVERY) continue
                    val peer = JoduJson.payload<DiscoveryPayload>(msg) ?: continue
                    if (peer.deviceId == deviceId) continue
                    if (!peer.role.equals("desktop", ignoreCase = true)) continue
                    onDesktopFound(peer)
                } catch (_: Exception) {
                    delay(200)
                }
            }
        }

        announceJob = scope.launch(Dispatchers.IO) {
            while (isActive) {
                try {
                    val payload = DiscoveryPayload(
                        deviceId = deviceId,
                        deviceName = deviceName,
                        role = "android",
                        ip = localIp(),
                        wsPort = JoduPorts.WEB_SOCKET,
                        httpPort = JoduPorts.FILE_HTTP,
                    )
                    val bytes = JoduJson.message(EventTypes.DISCOVERY, payload).toByteArray()
                    val packet = DatagramPacket(
                        bytes,
                        bytes.size,
                        InetAddress.getByName("255.255.255.255"),
                        JoduPorts.DISCOVERY,
                    )
                    s.send(packet)
                } catch (_: Exception) {
                    // retry
                }
                delay(2000)
            }
        }
    }

    fun stop() {
        listenJob?.cancel()
        announceJob?.cancel()
        socket?.close()
        socket = null
    }

    companion object {
        fun localIp(): String {
            return try {
                NetworkInterface.getNetworkInterfaces().toList()
                    .flatMap { it.inetAddresses.toList() }
                    .firstOrNull { !it.isLoopbackAddress && it.hostAddress?.contains(':') == false }
                    ?.hostAddress
                    ?: "127.0.0.1"
            } catch (_: Exception) {
                "127.0.0.1"
            }
        }
    }
}
