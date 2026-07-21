package com.jodu.app.network

import com.jodu.app.protocol.DiscoveryPayload
import com.jodu.app.protocol.EventTypes
import com.jodu.app.protocol.JoduJson
import com.jodu.app.protocol.JoduPorts
import com.jodu.app.protocol.PairPayload
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.net.DatagramPacket
import java.net.DatagramSocket
import java.net.InetAddress
import java.net.InetSocketAddress
import java.net.NetworkInterface
import java.util.concurrent.ConcurrentHashMap

class DiscoveryService(
    private val scope: CoroutineScope,
    private val deviceId: String,
    private val deviceName: String,
    private val onPeersChanged: (List<DiscoveryPayload>) -> Unit,
    private val onPairRequest: (PairPayload) -> Unit,
    private val onPairResponse: (PairPayload) -> Unit,
) {
    private var socket: DatagramSocket? = null
    private var listenJob: Job? = null
    private var announceJob: Job? = null
    private var pruneJob: Job? = null
    private val peers = ConcurrentHashMap<String, Pair<DiscoveryPayload, Long>>()

    fun start() {
        if (socket != null) return
        val s = DatagramSocket(null).apply {
            reuseAddress = true
            broadcast = true
            bind(InetSocketAddress(JoduPorts.DISCOVERY))
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
                    when (msg.type) {
                        EventTypes.DISCOVERY -> {
                            val peer = JoduJson.payload<DiscoveryPayload>(msg) ?: continue
                            if (peer.deviceId == deviceId) continue
                            if (!peer.role.equals("desktop", ignoreCase = true)) continue
                            val ip = peer.ip.ifBlank { packet.address.hostAddress.orEmpty() }
                            peers[peer.deviceId] = peer.copy(ip = ip) to System.currentTimeMillis()
                            onPeersChanged(currentPeers())
                        }
                        EventTypes.PAIR_REQUEST -> {
                            val req = JoduJson.payload<PairPayload>(msg) ?: continue
                            if (req.targetDeviceId != deviceId) continue
                            val fixed = if (req.fromIp.isBlank()) {
                                req.copy(fromIp = packet.address.hostAddress.orEmpty())
                            } else {
                                req
                            }
                            onPairRequest(fixed)
                        }
                        EventTypes.PAIR_RESPONSE -> {
                            val res = JoduJson.payload<PairPayload>(msg) ?: continue
                            if (res.targetDeviceId != deviceId) continue
                            onPairResponse(res)
                        }
                    }
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

        pruneJob = scope.launch(Dispatchers.IO) {
            while (isActive) {
                delay(3000)
                val cutoff = System.currentTimeMillis() - 8000
                var removed = false
                peers.entries.removeIf {
                    val stale = it.value.second < cutoff
                    if (stale) removed = true
                    stale
                }
                if (removed) onPeersChanged(currentPeers())
            }
        }
    }

    fun requestPair(target: DiscoveryPayload) {
        val payload = localPair(target.deviceId)
        sendTo(target.ip, EventTypes.PAIR_REQUEST, payload)
    }

    fun respondPair(request: PairPayload, accepted: Boolean) {
        val payload = localPair(request.fromDeviceId).copy(accepted = accepted)
        sendTo(request.fromIp, EventTypes.PAIR_RESPONSE, payload)
    }

    fun currentPeers(): List<DiscoveryPayload> =
        peers.values.map { it.first }.sortedBy { it.deviceName.lowercase() }

    private fun localPair(targetDeviceId: String) = PairPayload(
        fromDeviceId = deviceId,
        fromDeviceName = deviceName,
        fromRole = "android",
        fromIp = localIp(),
        wsPort = JoduPorts.WEB_SOCKET,
        httpPort = JoduPorts.FILE_HTTP,
        targetDeviceId = targetDeviceId,
    )

    private fun sendTo(ip: String, type: String, payload: PairPayload) {
        val s = socket ?: return
        try {
            val bytes = JoduJson.message(type, payload).toByteArray()
            val packet = DatagramPacket(
                bytes,
                bytes.size,
                InetAddress.getByName(ip),
                JoduPorts.DISCOVERY,
            )
            s.send(packet)
        } catch (_: Exception) {
            // ignore
        }
    }

    fun stop() {
        listenJob?.cancel()
        announceJob?.cancel()
        pruneJob?.cancel()
        socket?.close()
        socket = null
        peers.clear()
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
