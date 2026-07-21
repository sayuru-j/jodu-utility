package com.jodu.app.network

import android.content.Context
import android.net.wifi.WifiManager
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
    private val context: Context,
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
    private var multicastLock: WifiManager.MulticastLock? = null
    private val peers = ConcurrentHashMap<String, Pair<DiscoveryPayload, Long>>()

    fun start() {
        if (socket != null) return

        val wifi = context.applicationContext.getSystemService(Context.WIFI_SERVICE) as WifiManager
        multicastLock = wifi.createMulticastLock("jodu-discovery").also {
            it.setReferenceCounted(false)
            it.acquire()
        }

        val s = DatagramSocket(null).apply {
            reuseAddress = true
            broadcast = true
            soTimeout = 0
            bind(InetSocketAddress(JoduPorts.DISCOVERY))
        }
        socket = s

        listenJob = scope.launch(Dispatchers.IO) {
            val buffer = ByteArray(8192)
            while (isActive) {
                try {
                    val packet = DatagramPacket(buffer, buffer.size)
                    s.receive(packet)
                    val raw = String(packet.data, 0, packet.length, Charsets.UTF_8)
                    val msg = runCatching { JoduJson.parse(raw) }.getOrNull() ?: continue
                    val sourceIp = packet.address?.hostAddress?.substringBefore('%').orEmpty()

                    when (msg.type) {
                        EventTypes.DISCOVERY -> {
                            val peer = JoduJson.payload<DiscoveryPayload>(msg) ?: continue
                            if (peer.deviceId == deviceId) continue
                            if (!peer.role.equals("desktop", ignoreCase = true)) continue
                            val ip = sourceIp.ifBlank { peer.ip }
                            // Drop stale identities that share this LAN IP (deviceId rotates on restart).
                            peers.entries.removeIf { (id, entry) ->
                                id != peer.deviceId && entry.first.ip == ip
                            }
                            peers[peer.deviceId] = peer.copy(ip = ip) to System.currentTimeMillis()
                            onPeersChanged(currentPeers())
                            // Unicast our identity back — more reliable than broadcast alone.
                            if (ip.isNotBlank()) {
                                sendTo(ip, EventTypes.DISCOVERY, localDiscovery())
                            }
                        }
                        EventTypes.PAIR_REQUEST -> {
                            val req = JoduJson.payload<PairPayload>(msg) ?: continue
                            val targeted = req.targetDeviceId.isBlank() ||
                                req.targetDeviceId.equals(deviceId, ignoreCase = true)
                            if (!targeted) continue
                            onPairRequest(
                                req.copy(fromIp = sourceIp.ifBlank { req.fromIp }),
                            )
                        }
                        EventTypes.PAIR_RESPONSE -> {
                            val res = JoduJson.payload<PairPayload>(msg) ?: continue
                            val forUs = res.targetDeviceId.isBlank() ||
                                res.targetDeviceId.equals(deviceId, ignoreCase = true)
                            if (!forUs) continue
                            onPairResponse(
                                res.copy(fromIp = sourceIp.ifBlank { res.fromIp }),
                            )
                        }
                    }
                } catch (_: Exception) {
                    delay(150)
                }
            }
        }

        announceJob = scope.launch(Dispatchers.IO) {
            while (isActive) {
                try {
                    announce()
                } catch (_: Exception) {
                    // retry
                }
                delay(1500)
            }
        }

        pruneJob = scope.launch(Dispatchers.IO) {
            while (isActive) {
                delay(3000)
                val cutoff = System.currentTimeMillis() - 10_000
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
        // Fire a short burst — UDP pair packets are easily lost on some APs.
        scope.launch(Dispatchers.IO) {
            repeat(4) { attempt ->
                sendTo(target.ip, EventTypes.PAIR_REQUEST, payload)
                broadcast(EventTypes.PAIR_REQUEST, payload)
                if (attempt < 3) delay(350)
            }
        }
    }

    fun respondPair(request: PairPayload, accepted: Boolean) {
        val payload = localPair(request.fromDeviceId).copy(accepted = accepted)
        sendTo(request.fromIp, EventTypes.PAIR_RESPONSE, payload)
        broadcast(EventTypes.PAIR_RESPONSE, payload)
    }

    fun currentPeers(): List<DiscoveryPayload> =
        peers.values.map { it.first }.sortedBy { it.deviceName.lowercase() }

    private fun announce() {
        val payload = localDiscovery()
        broadcast(EventTypes.DISCOVERY, payload)
        // Keep desktops visible on the phone by refreshing known peers over unicast.
        for ((peer, _) in peers.values) {
            if (peer.ip.isNotBlank()) sendTo(peer.ip, EventTypes.DISCOVERY, payload)
        }
    }

    private fun localDiscovery() = DiscoveryPayload(
        deviceId = deviceId,
        deviceName = deviceName,
        role = "android",
        ip = localIp(),
        wsPort = JoduPorts.WEB_SOCKET,
        httpPort = JoduPorts.FILE_HTTP,
    )

    private fun localPair(targetDeviceId: String) = PairPayload(
        fromDeviceId = deviceId,
        fromDeviceName = deviceName,
        fromRole = "android",
        fromIp = localIp(),
        wsPort = JoduPorts.WEB_SOCKET,
        httpPort = JoduPorts.FILE_HTTP,
        targetDeviceId = targetDeviceId,
    )

    private inline fun <reified T> broadcast(type: String, payload: T) {
        val s = socket ?: return
        val bytes = JoduJson.message(type, payload).toByteArray()
        val targets = buildList {
            add(InetAddress.getByName("255.255.255.255"))
            runCatching { subnetBroadcast() }.getOrNull()?.let { add(it) }
        }
        for (addr in targets.distinctBy { it.hostAddress }) {
            try {
                s.send(DatagramPacket(bytes, bytes.size, addr, JoduPorts.DISCOVERY))
            } catch (_: Exception) {
                // ignore
            }
        }
    }

    private inline fun <reified T> sendTo(ip: String, type: String, payload: T) {
        val s = socket ?: return
        if (ip.isBlank()) return
        try {
            val bytes = JoduJson.message(type, payload).toByteArray()
            s.send(
                DatagramPacket(
                    bytes,
                    bytes.size,
                    InetAddress.getByName(ip),
                    JoduPorts.DISCOVERY,
                ),
            )
        } catch (_: Exception) {
            // ignore
        }
    }

    private fun subnetBroadcast(): InetAddress? {
        val ifaces = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
        for (iface in ifaces) {
            if (!iface.isUp || iface.isLoopback) continue
            for (addr in iface.interfaceAddresses) {
                val broadcast = addr.broadcast ?: continue
                if (broadcast.hostAddress?.contains(':') == true) continue
                return broadcast
            }
        }
        return null
    }

    fun stop() {
        listenJob?.cancel()
        announceJob?.cancel()
        pruneJob?.cancel()
        socket?.close()
        socket = null
        peers.clear()
        runCatching { multicastLock?.release() }
        multicastLock = null
    }

    companion object {
        fun localIp(): String {
            return try {
                val ifaces = NetworkInterface.getNetworkInterfaces()?.toList().orEmpty()
                val candidates = ifaces
                    .filter { it.isUp && !it.isLoopback }
                    .flatMap { iface ->
                        iface.inetAddresses.toList().mapNotNull { addr ->
                            val host = addr.hostAddress?.substringBefore('%') ?: return@mapNotNull null
                            if (addr.isLoopbackAddress || host.contains(':')) return@mapNotNull null
                            host to iface.name.lowercase()
                        }
                    }

                candidates.firstOrNull { (_, name) ->
                    name.startsWith("wlan") || name.startsWith("wifi") || name.startsWith("ap")
                }?.first
                    ?: candidates.firstOrNull()?.first
                    ?: "127.0.0.1"
            } catch (_: Exception) {
                "127.0.0.1"
            }
        }
    }
}
