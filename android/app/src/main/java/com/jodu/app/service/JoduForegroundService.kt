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
import com.jodu.app.network.JoduWebSocketClient
import com.jodu.app.protocol.ClipboardPayload
import com.jodu.app.protocol.DiscoveryPayload
import com.jodu.app.protocol.EventTypes
import com.jodu.app.protocol.JoduJson
import com.jodu.app.protocol.JoduMessage
import com.jodu.app.protocol.MediaControlPayload
import com.jodu.app.protocol.MediaStatePayload
import com.jodu.app.protocol.OtpPayload
import com.jodu.app.protocol.TelemetryPayload
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.SupervisorJob
import kotlinx.coroutines.cancel
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import java.util.UUID

class JoduForegroundService : Service() {
    private val scope = CoroutineScope(SupervisorJob() + Dispatchers.Main.immediate)
    private val deviceId = UUID.randomUUID().toString().replace("-", "").take(12)

    private lateinit var discovery: DiscoveryService
    private lateinit var socket: JoduWebSocketClient
    private lateinit var fileServer: FileHttpServer
    private lateinit var media: MediaControllerBridge
    private lateinit var ping: PingAlertHelper
    private lateinit var clipboard: ClipboardManager

    private var desktop: DiscoveryPayload? = null
    private var lastClip: String? = null

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
        createChannel()
        startForeground(NOTIFICATION_ID, buildNotification(getString(R.string.service_waiting)))

        clipboard = getSystemService(CLIPBOARD_SERVICE) as ClipboardManager
        media = MediaControllerBridge(this)
        ping = PingAlertHelper(this)
        fileServer = FileHttpServer()

        socket = JoduWebSocketClient(
            scope = scope,
            onMessage = ::onSocketMessage,
            onConnectionChanged = { connected ->
                val text = if (connected) {
                    getString(R.string.service_running)
                } else {
                    getString(R.string.service_waiting)
                }
                val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
                nm.notify(NOTIFICATION_ID, buildNotification(text))
            },
        )

        discovery = DiscoveryService(
            scope = scope,
            deviceId = deviceId,
            deviceName = Settings.Global.getString(contentResolver, Settings.Global.DEVICE_NAME)
                ?: Build.MODEL,
            onDesktopFound = { peer ->
                if (desktop?.deviceId == peer.deviceId && socket.isConnected) return@DiscoveryService
                desktop = peer
                socket.connect(peer.ip, peer.wsPort)
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
            ACTION_STOP_PING -> ping.stop()
            ACTION_STOP_SERVICE -> {
                stopSelf()
                return START_NOT_STICKY
            }
        }
        return START_STICKY
    }

    override fun onDestroy() {
        instance = null
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

    fun sendOtp(payload: OtpPayload) {
        socket.send(EventTypes.OTP_DETECTED, payload)
        lastClip = payload.code
    }

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
                        .setContentText("Phone alert playing — tap to dismiss")
                        .setContentIntent(
                            PendingIntent.getService(
                                this,
                                2,
                                Intent(this, JoduForegroundService::class.java).setAction(ACTION_STOP_PING),
                                PendingIntent.FLAG_UPDATE_CURRENT or PendingIntent.FLAG_IMMUTABLE,
                            ),
                        )
                        .setAutoCancel(true)
                        .build(),
                )
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
        val channel = NotificationChannel(
            CHANNEL_ID,
            getString(R.string.service_channel),
            NotificationManager.IMPORTANCE_LOW,
        )
        val nm = getSystemService(NOTIFICATION_SERVICE) as NotificationManager
        nm.createNotificationChannel(channel)
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

    val isLinked: Boolean
        get() = ::socket.isInitialized && socket.isConnected

    companion object {
        const val CHANNEL_ID = "jodu_bridge"
        const val NOTIFICATION_ID = 1001
        const val PING_NOTIFICATION_ID = 1002
        const val ACTION_STOP_PING = "com.jodu.app.STOP_PING"
        const val ACTION_STOP_SERVICE = "com.jodu.app.STOP_SERVICE"

        @Volatile
        var instance: JoduForegroundService? = null
            private set
    }
}
