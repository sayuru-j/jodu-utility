package com.jodu.app.service

import android.app.Notification
import android.app.NotificationChannel
import android.app.NotificationManager
import android.app.PendingIntent
import android.app.Service
import android.content.BroadcastReceiver
import android.content.ClipData
import android.content.ClipboardManager
import android.content.Context
import android.content.Intent
import android.content.IntentFilter
import android.net.wifi.WifiManager
import android.os.BatteryManager
import android.os.Build
import android.os.IBinder
import android.provider.Settings
import androidx.core.app.NotificationCompat
import com.jodu.app.MainActivity
import com.jodu.app.R
import com.jodu.app.media.MediaControllerBridge
import com.jodu.app.network.DiscoveryService
import com.jodu.app.network.FileHttpServer
import com.jodu.app.network.FileUploadClient
import com.jodu.app.network.JoduWebSocketClient
import com.jodu.app.protocol.ClipboardPayload
import com.jodu.app.protocol.DiscoveryPayload
import com.jodu.app.protocol.EventTypes
import com.jodu.app.protocol.JoduJson
import com.jodu.app.protocol.JoduMessage
import com.jodu.app.protocol.JoduPorts
import com.jodu.app.protocol.MediaControlPayload
import com.jodu.app.protocol.MediaStatePayload
import com.jodu.app.protocol.OtpPayload
import com.jodu.app.protocol.PairPayload
import com.jodu.app.protocol.TelemetryPayload
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.util.UUID
import java.util.concurrent.CopyOnWriteArrayList

class JoduForegroundService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private lateinit var deviceId: String

    private lateinit var discovery: DiscoveryService
    private lateinit var socket: JoduWebSocketClient
    private lateinit var fileServer: FileHttpServer
    private lateinit var media: MediaControllerBridge
    private lateinit var ping: PingAlertHelper
    private lateinit var clipboard: ClipboardManager

    private var desktop: DiscoveryPayload? = null
    private var lastClip: String? = null
    @Volatile var lanPeers: List<DiscoveryPayload> = emptyList()
        private set
    @Volatile var incomingPair: PairPayload? = null
        private set
    @Volatile var outgoingPairDeviceId: String? = null
        private set
    @Volatile var pairStatus: String = "idle"
        private set
    @Volatile var lastTransferNote: String? = null
        private set

    private val listeners = CopyOnWriteArrayList<() -> Unit>()

    private val batteryReceiver = object : BroadcastReceiver() {
        override fun onReceive(context: Context?, intent: Intent?) {
            pushTelemetry()
        }
    }

    private val clipListener = ClipboardManager.OnPrimaryClipChangedListener {
        val text = clipboard.primaryClip
            ?.takeIf { it.itemCount > 0 }
            ?.getItemAt(0)
            ?.coerceToText(this)
            ?.toString()
            ?.trim()
            .orEmpty()
        if (text.isEmpty() || text == lastClip) return@OnPrimaryClipChangedListener
        lastClip = text
        socket.send(EventTypes.CLIPBOARD_UPDATE, ClipboardPayload(text = text, source = "phone"))
    }

    override fun onCreate() {
        super.onCreate()
        instance = this
        deviceId = resolveDeviceId()
        createChannel()
        getSharedPreferences(PREFS, MODE_PRIVATE)
            .edit()
            .putBoolean(PREF_BRIDGE_ENABLED, true)
            .apply()
        startForeground(NOTIFICATION_ID, buildNotification(getString(R.string.service_running)))

        clipboard = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
        media = MediaControllerBridge(this)
        ping = PingAlertHelper(this)
        fileServer = FileHttpServer(this).also { server ->
            server.onFileReceived = { path ->
                lastTransferNote = "received file"
                notifyUi()
            }
        }

        socket = JoduWebSocketClient(
            scope = scope,
            onMessage = ::onSocketMessage,
            onConnectionChanged = { connected ->
                if (connected) {
                    pairStatus = "linked"
                    incomingPair = null
                    outgoingPairDeviceId = null
                    clearPairRequestNotification()
                } else if (pairStatus == "linked") {
                    pairStatus = "idle"
                }
                refreshOngoingNotification()
                notifyUi()
            },
        )

        discovery = DiscoveryService(
            context = this,
            scope = scope,
            deviceId = deviceId,
            deviceName = Settings.Global.getString(contentResolver, Settings.Global.DEVICE_NAME)
                ?: Build.MODEL,
            onPeersChanged = { peers ->
                lanPeers = peers
                val linkedPeer = desktop
                if (linkedPeer != null) {
                    peers.firstOrNull { it.ip == linkedPeer.ip }?.let { fresh ->
                        desktop = fresh
                    }
                }
                notifyUi()
            },
            onPairRequest = { req ->
                scope.launch(Dispatchers.Main) {
                    presentIncomingPair(req)
                }
            },
            onPairResponse = { res ->
                scope.launch(Dispatchers.Main) {
                    if (outgoingPairDeviceId == null || res.fromDeviceId != outgoingPairDeviceId) return@launch
                    if (res.accepted == true) {
                        val port = res.wsPort.takeIf { it > 0 } ?: JoduPorts.WEB_SOCKET
                        desktop = DiscoveryPayload(
                            deviceId = res.fromDeviceId,
                            deviceName = res.fromDeviceName,
                            role = res.fromRole,
                            ip = res.fromIp,
                            wsPort = port,
                            httpPort = res.httpPort,
                        )
                        pairStatus = "accepted"
                        outgoingPairDeviceId = null
                        socket.connect(res.fromIp, port)
                        refreshOngoingNotification()
                    } else {
                        pairStatus = "rejected"
                        outgoingPairDeviceId = null
                    }
                    notifyUi()
                }
            },
        )

        clipboard.addPrimaryClipChangedListener(clipListener)
        registerReceiver(batteryReceiver, IntentFilter(Intent.ACTION_BATTERY_CHANGED))

        discovery.start()
        runCatching { fileServer.start() }

        scope.launch {
            while (isActive) {
                pushTelemetry()
                pushMediaState()
                delay(5_000)
            }
        }
    }

    override fun onStartCommand(intent: Intent?, flags: Int, startId: Int): Int {
        when (intent?.action) {
            ACTION_STOP_PING -> stopPing()
            ACTION_ACCEPT_PAIR -> acceptPair()
            ACTION_REJECT_PAIR -> rejectPair()
            ACTION_STOP_SERVICE -> {
                getSharedPreferences(PREFS, MODE_PRIVATE)
                    .edit()
                    .putBoolean(PREF_BRIDGE_ENABLED, false)
                    .apply()
                stopSelf()
                return START_NOT_STICKY
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        instance = null
        clearPairRequestNotification()
        runCatching { unregisterReceiver(batteryReceiver) }
        clipboard.removePrimaryClipChangedListener(clipListener)
        discovery.stop()
        socket.disconnect()
        fileServer.stop()
        ping.stop()
        scope.cancel()
        super.onDestroy()
    }

    override fun onBind(intent: Intent?): IBinder? = null

    fun stopPing() {
        ping.stop()
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(PING_NOTIFICATION_ID)
        notifyUi()
    }

    val isPinging: Boolean
        get() = ::ping.isInitialized && ping.isPinging

    fun sendFilesToDesktop(uris: List<android.net.Uri>) {
        val peer = desktop ?: return
        if (!isLinked || uris.isEmpty()) return
        scope.launch {
            lastTransferNote = "sending ${uris.size}…"
            notifyUi()
            var ok = 0
            for (uri in uris) {
                val result = FileUploadClient.upload(this@JoduForegroundService, peer, uri)
                if (result.isSuccess) ok++
            }
            lastTransferNote = if (ok == uris.size) "sent $ok file(s)" else "sent $ok/${uris.size}"
            notifyUi()
        }
    }

    fun sendOtp(payload: OtpPayload) {
        socket.send(EventTypes.OTP_DETECTED, payload)
        lastClip = payload.code
    }

    fun addUiListener(listener: () -> Unit) {
        listeners += listener
    }

    fun removeUiListener(listener: () -> Unit) {
        listeners -= listener
    }

    fun requestPair(deviceId: String) {
        val target = lanPeers.firstOrNull { it.deviceId == deviceId } ?: return
        outgoingPairDeviceId = deviceId
        pairStatus = "outgoing"
        discovery.requestPair(target)
        notifyUi()
    }

    fun acceptPair() {
        val req = incomingPair ?: return
        val port = req.wsPort.takeIf { it > 0 } ?: JoduPorts.WEB_SOCKET
        desktop = DiscoveryPayload(
            deviceId = req.fromDeviceId,
            deviceName = req.fromDeviceName,
            role = req.fromRole,
            ip = req.fromIp,
            wsPort = port,
            httpPort = req.httpPort,
        )
        discovery.respondPair(req, accepted = true)
        incomingPair = null
        pairStatus = "accepted"
        clearPairRequestNotification()
        socket.connect(req.fromIp, port)
        refreshOngoingNotification()
        notifyUi()
    }

    fun rejectPair() {
        val req = incomingPair ?: return
        discovery.respondPair(req, accepted = false)
        incomingPair = null
        pairStatus = "idle"
        clearPairRequestNotification()
        notifyUi()
    }

    private fun presentIncomingPair(req: PairPayload) {
        // Keep the latest request; bridge-on means we always surface it immediately.
        incomingPair = req
        pairStatus = "incoming"
        showPairRequestNotification(req)
        runCatching {
            startActivity(
                Intent(this, MainActivity::class.java)
                    .addFlags(
                        Intent.FLAG_ACTIVITY_NEW_TASK or
                            Intent.FLAG_ACTIVITY_SINGLE_TOP or
                            Intent.FLAG_ACTIVITY_REORDER_TO_FRONT,
                    ),
            )
        }
        notifyUi()
    }

    private fun notifyUi() {
        listeners.forEach { runCatching { it.invoke() } }
    }

    private fun refreshOngoingNotification() {
        val text = when {
            isLinked -> getString(R.string.service_paired, desktop?.deviceName ?: "desktop")
            pairStatus == "accepted" && !isLinked -> {
                val err = if (::socket.isInitialized) socket.lastError else null
                if (err.isNullOrBlank()) "connecting to desktop…" else "connect failed · retrying"
            }
            else -> getString(R.string.service_running)
        }
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.notify(NOTIFICATION_ID, buildNotification(text))
    }

    private fun showPairRequestNotification(req: PairPayload) {
        val openIntent = Intent(this, MainActivity::class.java)
            .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP)
        val open = PendingIntent.getActivity(
            this,
            1,
            openIntent,
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val accept = PendingIntent.getService(
            this,
            3,
            Intent(this, JoduForegroundService::class.java).setAction(ACTION_ACCEPT_PAIR),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        val decline = PendingIntent.getService(
            this,
            4,
            Intent(this, JoduForegroundService::class.java).setAction(ACTION_REJECT_PAIR),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )

        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.notify(
            PAIR_NOTIFICATION_ID,
            NotificationCompat.Builder(this, PAIR_CHANNEL_ID)
                .setSmallIcon(R.drawable.ic_stat_jodu)
                .setContentTitle(getString(R.string.pair_request_title))
                .setContentText(getString(R.string.pair_request_body, req.fromDeviceName))
                .setContentIntent(open)
                .setFullScreenIntent(open, true)
                .setPriority(NotificationCompat.PRIORITY_MAX)
                .setCategory(NotificationCompat.CATEGORY_CALL)
                .setVisibility(NotificationCompat.VISIBILITY_PUBLIC)
                .setAutoCancel(true)
                .setOnlyAlertOnce(false)
                .addAction(0, getString(R.string.action_decline), decline)
                .addAction(0, getString(R.string.action_accept), accept)
                .build(),
        )
    }

    private fun clearPairRequestNotification() {
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.cancel(PAIR_NOTIFICATION_ID)
    }

    val pairedDesktop: DiscoveryPayload?
        get() = desktop

    private fun onSocketMessage(message: JoduMessage) {
        when (message.type) {
            EventTypes.MEDIA_CONTROL -> {
                val action = JoduJson.payload<MediaControlPayload>(message)?.action ?: return
                media.handle(action)
                pushMediaState()
            }

            EventTypes.PING_DEVICE -> {
                ping.start()
                val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
                nm.notify(
                    PING_NOTIFICATION_ID,
                    NotificationCompat.Builder(this, CHANNEL_ID)
                        .setSmallIcon(R.drawable.ic_stat_jodu)
                        .setContentTitle("JODU ping")
                        .setContentText("Phone alert playing — tap Stop in the app")
                        .setContentIntent(
                            PendingIntent.getActivity(
                                this,
                                2,
                                Intent(this, MainActivity::class.java)
                                    .addFlags(Intent.FLAG_ACTIVITY_SINGLE_TOP or Intent.FLAG_ACTIVITY_CLEAR_TOP),
                                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
                            ),
                        )
                        .addAction(
                            0,
                            getString(R.string.action_stop_ping),
                            PendingIntent.getService(
                                this,
                                5,
                                Intent(this, JoduForegroundService::class.java).setAction(ACTION_STOP_PING),
                                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
                            ),
                        )
                        .setOngoing(true)
                        .setAutoCancel(false)
                        .build(),
                )
                runCatching {
                    startActivity(
                        Intent(this, MainActivity::class.java)
                            .addFlags(
                                Intent.FLAG_ACTIVITY_NEW_TASK or
                                    Intent.FLAG_ACTIVITY_SINGLE_TOP or
                                    Intent.FLAG_ACTIVITY_REORDER_TO_FRONT,
                            ),
                    )
                }
                notifyUi()
            }

            EventTypes.CLIPBOARD_UPDATE -> {
                val text = JoduJson.payload<ClipboardPayload>(message)?.text ?: return
                lastClip = text
                clipboard.setPrimaryClip(ClipData.newPlainText("jodu", text))
            }
        }
    }

    private fun pushTelemetry() {
        val battery = registerReceiver(null, IntentFilter(Intent.ACTION_BATTERY_CHANGED))
        val level = battery?.getIntExtra(BatteryManager.EXTRA_LEVEL, -1) ?: -1
        val scale = battery?.getIntExtra(BatteryManager.EXTRA_SCALE, 100) ?: 100
        val status = battery?.getIntExtra(BatteryManager.EXTRA_STATUS, -1) ?: -1
        val charging = status == BatteryManager.BATTERY_STATUS_CHARGING ||
            status == BatteryManager.BATTERY_STATUS_FULL
        val percent = if (level >= 0 && scale > 0) (level * 100) / scale else 0

        val wifi = applicationContext.getSystemService(WIFI_SERVICE) as WifiManager
        @Suppress("DEPRECATION")
        val ssid = wifi.connectionInfo?.ssid?.trim('"')?.takeIf { it != "<unknown ssid>" }

        socket.send(
            EventTypes.TELEMETRY,
            TelemetryPayload(
                batteryPercent = percent,
                isCharging = charging,
                wifiSsid = ssid,
                deviceName = Build.MODEL,
            ),
        )
    }

    private fun pushMediaState() {
        val state: MediaStatePayload = media.activeState()
        socket.send(EventTypes.MEDIA_STATE, state)
    }

    private fun createChannel() {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(
            NotificationChannel(
                CHANNEL_ID,
                getString(R.string.service_channel),
                NotificationManager.IMPORTANCE_LOW,
            ),
        )
        nm.createNotificationChannel(
            NotificationChannel(
                PAIR_CHANNEL_ID,
                getString(R.string.pair_channel),
                NotificationManager.IMPORTANCE_HIGH,
            ).apply {
                description = getString(R.string.pair_channel_desc)
            },
        )
    }

    private fun buildNotification(content: String): Notification {
        val open = PendingIntent.getActivity(
            this,
            0,
            Intent(this, MainActivity::class.java),
            PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
        )
        return NotificationCompat.Builder(this, CHANNEL_ID)
            .setSmallIcon(R.drawable.ic_stat_jodu)
            .setContentTitle("JODU")
            .setContentText(content)
            .setContentIntent(open)
            .setOngoing(true)
            .build()
    }

    private fun resolveDeviceId(): String {
        val prefs = getSharedPreferences(PREFS, MODE_PRIVATE)
        val existing = prefs.getString(PREF_DEVICE_ID, null)?.trim()
        if (!existing.isNullOrBlank()) return existing.take(12)
        val created = UUID.randomUUID().toString().replace("-", "").take(12)
        prefs.edit().putString(PREF_DEVICE_ID, created).apply()
        return created
    }

    val isLinked: Boolean
        get() = ::socket.isInitialized && socket.isConnected

    companion object {
        const val CHANNEL_ID = "jodu_bridge"
        const val PAIR_CHANNEL_ID = "jodu_pair"
        const val NOTIFICATION_ID = 1001
        const val PING_NOTIFICATION_ID = 1002
        const val PAIR_NOTIFICATION_ID = 1003
        const val ACTION_STOP_PING = "com.jodu.app.STOP_PING"
        const val ACTION_STOP_SERVICE = "com.jodu.app.STOP_SERVICE"
        const val ACTION_ACCEPT_PAIR = "com.jodu.app.ACCEPT_PAIR"
        const val ACTION_REJECT_PAIR = "com.jodu.app.REJECT_PAIR"

        const val PREFS = "jodu_prefs"
        const val PREF_BRIDGE_ENABLED = "bridge_enabled"
        const val PREF_DEVICE_ID = "device_id"

        @Volatile
        var instance: JoduForegroundService? = null
            private set
    }
}
