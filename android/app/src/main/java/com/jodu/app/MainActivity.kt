package com.jodu.app

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.net.Uri
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.ProgressBar
import android.widget.TextView
import androidx.activity.result.contract.ActivityResultContracts
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import androidx.core.view.ViewCompat
import androidx.core.view.WindowCompat
import androidx.core.view.WindowInsetsCompat
import androidx.core.view.updatePadding
import com.google.android.material.materialswitch.MaterialSwitch
import com.jodu.app.protocol.DiscoveryPayload
import com.jodu.app.service.JoduForegroundService

class MainActivity : AppCompatActivity() {
    private lateinit var status: TextView
    private lateinit var linkLabel: TextView
    private lateinit var linkGlyph: View
    private lateinit var pairBanner: View
    private lateinit var pairFrom: TextView
    private lateinit var pingBanner: View
    private lateinit var devicesEmpty: TextView
    private lateinit var deviceList: LinearLayout
    private lateinit var rootContent: View
    private lateinit var bridgeSwitch: MaterialSwitch
    private lateinit var bridgeLabel: TextView
    private lateinit var btnSendFile: Button
    private lateinit var filesHint: TextView
    private lateinit var filesProgress: ProgressBar

    private var updatingSwitch = false
    private val prefs by lazy { getSharedPreferences(JoduForegroundService.PREFS, MODE_PRIVATE) }
    private val uiListener = { runOnUiThread { refreshStatus() } }

    private val pickFiles = registerForActivityResult(
        ActivityResultContracts.OpenMultipleDocuments(),
    ) { uris: List<Uri> ->
        if (uris.isEmpty()) return@registerForActivityResult
        uris.forEach { uri ->
            runCatching {
                contentResolver.takePersistableUriPermission(
                    uri,
                    Intent.FLAG_GRANT_READ_URI_PERMISSION,
                )
            }
        }
        JoduForegroundService.instance?.sendFilesToDesktop(uris)
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        WindowCompat.setDecorFitsSystemWindows(window, false)
        setContentView(R.layout.activity_main)

        rootContent = findViewById(R.id.rootContent)
        status = findViewById(R.id.status)
        linkLabel = findViewById(R.id.linkLabel)
        linkGlyph = findViewById(R.id.linkGlyph)
        pairBanner = findViewById(R.id.pairBanner)
        pairFrom = findViewById(R.id.pairFrom)
        pingBanner = findViewById(R.id.pingBanner)
        devicesEmpty = findViewById(R.id.devicesEmpty)
        deviceList = findViewById(R.id.deviceList)
        bridgeSwitch = findViewById(R.id.bridgeSwitch)
        bridgeLabel = findViewById(R.id.bridgeLabel)
        btnSendFile = findViewById(R.id.btnSendFile)
        filesHint = findViewById(R.id.filesHint)
        filesProgress = findViewById(R.id.filesProgress)

        val sidePad = dp(20)
        ViewCompat.setOnApplyWindowInsetsListener(rootContent) { view, insets ->
            val bars = insets.getInsets(WindowInsetsCompat.Type.systemBars())
            view.updatePadding(
                left = sidePad + bars.left,
                top = bars.top,
                right = sidePad + bars.right,
                bottom = bars.bottom,
            )
            insets
        }

        bridgeSwitch.setOnCheckedChangeListener { _, checked ->
            if (updatingSwitch) return@setOnCheckedChangeListener
            prefs.edit().putBoolean(JoduForegroundService.PREF_BRIDGE_ENABLED, checked).apply()
            if (checked) {
                requestRuntimePermissions()
                startBridge()
            } else {
                stopBridge()
            }
            refreshStatus()
        }

        findViewById<Button>(R.id.btnNotifications).setOnClickListener {
            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
        findViewById<Button>(R.id.btnSettings).setOnClickListener {
            startActivity(Intent(this, SettingsActivity::class.java))
        }
        findViewById<Button>(R.id.btnAccept).setOnClickListener {
            JoduForegroundService.instance?.acceptPair()
        }
        findViewById<Button>(R.id.btnDecline).setOnClickListener {
            JoduForegroundService.instance?.rejectPair()
        }
        findViewById<Button>(R.id.btnStopPing).setOnClickListener {
            JoduForegroundService.instance?.stopPing()
        }
        btnSendFile.setOnClickListener {
            pickFiles.launch(arrayOf("*/*"))
        }

        requestRuntimePermissions()
        if (prefs.getBoolean(JoduForegroundService.PREF_BRIDGE_ENABLED, true)) {
            startBridge()
        }
        refreshStatus()
    }

    override fun onResume() {
        super.onResume()
        JoduForegroundService.instance?.addUiListener(uiListener)
        refreshStatus()
    }

    override fun onPause() {
        JoduForegroundService.instance?.removeUiListener(uiListener)
        super.onPause()
    }

    private fun startBridge() {
        ContextCompat.startForegroundService(
            this,
            Intent(this, JoduForegroundService::class.java),
        )
    }

    private fun stopBridge() {
        val intent = Intent(this, JoduForegroundService::class.java)
            .setAction(JoduForegroundService.ACTION_STOP_SERVICE)
        startService(intent)
    }

    private fun refreshStatus() {
        val service = JoduForegroundService.instance
        val bridgeEnabled = prefs.getBoolean(JoduForegroundService.PREF_BRIDGE_ENABLED, true)
        val linked = service?.isLinked == true
        val incoming = service?.incomingPair
        val pairedDesktop = service?.pairedDesktop
        val peers = mergePairedPeer(service?.lanPeers.orEmpty(), pairedDesktop, linked)
        val pairedName = pairedDesktop?.deviceName

        updatingSwitch = true
        bridgeSwitch.isChecked = bridgeEnabled
        updatingSwitch = false
        bridgeLabel.setText(if (bridgeEnabled) R.string.bridge_on else R.string.bridge_off)

        when {
            service?.isPinging == true -> status.setText(R.string.status_pinging)
            linked -> status.text = getString(R.string.service_paired, pairedName ?: "desktop")
            incoming != null -> status.setText(R.string.status_incoming)
            service?.pairStatus == "outgoing" -> status.setText(R.string.status_outgoing)
            service?.pairStatus == "accepted" -> status.setText(R.string.status_connecting)
            service != null -> status.setText(R.string.status_running)
            else -> status.setText(R.string.status_starting)
        }

        pingBanner.visibility = if (service?.isPinging == true) View.VISIBLE else View.GONE

        if (linked) {
            linkLabel.text = getString(R.string.service_paired, pairedName ?: "desktop")
            linkLabel.setTextColor(ContextCompat.getColor(this, R.color.fg))
            linkGlyph.setBackgroundResource(R.drawable.bg_glyph_on)
        } else {
            linkLabel.setText(
                if (bridgeEnabled) R.string.service_running else R.string.service_waiting,
            )
            linkLabel.setTextColor(ContextCompat.getColor(this, R.color.muted))
            linkGlyph.setBackgroundResource(R.drawable.bg_glyph)
        }

        if (incoming != null && bridgeEnabled) {
            pairBanner.visibility = View.VISIBLE
            pairFrom.text = "${incoming.fromDeviceName}\n${incoming.fromIp}"
        } else {
            pairBanner.visibility = View.GONE
        }

        btnSendFile.isEnabled = linked
        val transfer = service?.transferLabel ?: service?.lastTransferNote
        val percent = service?.transferPercent ?: -1
        filesHint.text = when {
            !linked -> getString(R.string.files_hint)
            !transfer.isNullOrBlank() -> transfer
            else -> getString(R.string.files_hint)
        }
        if (linked && percent in 0..100) {
            filesProgress.visibility = View.VISIBLE
            filesProgress.isIndeterminate = false
            filesProgress.progress = percent
        } else if (linked && !transfer.isNullOrBlank() && transfer.contains("…")) {
            filesProgress.visibility = View.VISIBLE
            filesProgress.isIndeterminate = true
        } else {
            filesProgress.visibility = View.GONE
            filesProgress.isIndeterminate = false
        }

        renderDevices(peers, service)
    }

    override fun onNewIntent(intent: Intent) {
        super.onNewIntent(intent)
        setIntent(intent)
        refreshStatus()
    }

    private fun mergePairedPeer(
        peers: List<DiscoveryPayload>,
        paired: DiscoveryPayload?,
        linked: Boolean,
    ): List<DiscoveryPayload> {
        val byIp = LinkedHashMap<String, DiscoveryPayload>()
        fun put(peer: DiscoveryPayload) {
            val key = peer.ip.ifBlank { peer.deviceId }.lowercase()
            byIp[key] = peer
        }
        peers.forEach(::put)
        if (linked && paired != null) put(paired)
        return byIp.values.toList()
    }

    private fun renderDevices(peers: List<DiscoveryPayload>, service: JoduForegroundService?) {
        val bridgeEnabled = prefs.getBoolean(JoduForegroundService.PREF_BRIDGE_ENABLED, true)
        deviceList.removeAllViews()
        if (peers.isEmpty()) {
            devicesEmpty.visibility = View.VISIBLE
            devicesEmpty.setText(
                if (bridgeEnabled) R.string.devices_empty else R.string.status_starting,
            )
            return
        }
        devicesEmpty.visibility = View.GONE

        val linked = service?.isLinked == true
        val outgoing = service?.outgoingPairDeviceId
        val pairedId = service?.pairedDesktop?.deviceId
        val busy = linked || !bridgeEnabled

        peers.forEach { peer ->
            val isPaired = peer.deviceId == pairedId && linked
            val isOutgoing = peer.deviceId == outgoing
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(12), dp(12), dp(12), dp(12))
                background = ContextCompat.getDrawable(this@MainActivity, R.drawable.bg_cell)
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT,
                ).apply { bottomMargin = dp(8) }
                isEnabled = !busy || isOutgoing || isPaired
                alpha = if (busy && !isPaired && !isOutgoing) 0.45f else 1f
                setOnClickListener {
                    when {
                        isPaired -> Unit
                        busy && !isOutgoing -> Unit
                        else -> service?.requestPair(peer.deviceId)
                    }
                }
            }

            val dot = View(this).apply {
                layoutParams = LinearLayout.LayoutParams(dp(7), dp(7)).apply { marginEnd = dp(10) }
                setBackgroundResource(
                    if (isPaired) R.drawable.bg_glyph_on else R.drawable.bg_glyph,
                )
            }

            val meta = LinearLayout(this).apply {
                orientation = LinearLayout.VERTICAL
                layoutParams = LinearLayout.LayoutParams(0, LinearLayout.LayoutParams.WRAP_CONTENT, 1f)
            }
            meta.addView(TextView(this).apply {
                text = peer.deviceName
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.fg))
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 15f)
                typeface = Typeface.DEFAULT_BOLD
            })
            meta.addView(TextView(this).apply {
                text = "${peer.ip} · ${peer.role}"
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.muted))
                typeface = Typeface.MONOSPACE
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
            })

            val action = TextView(this).apply {
                typeface = Typeface.MONOSPACE
                setTextSize(TypedValue.COMPLEX_UNIT_SP, 11f)
                setTextColor(ContextCompat.getColor(this@MainActivity, R.color.muted))
                text = when {
                    isPaired -> getString(R.string.label_paired)
                    peer.deviceId == outgoing -> "cancel"
                    else -> getString(R.string.action_pair)
                }
            }

            row.addView(dot)
            row.addView(meta)
            row.addView(action)
            deviceList.addView(row)
        }
    }

    private fun dp(value: Int): Int =
        TypedValue.applyDimension(TypedValue.COMPLEX_UNIT_DIP, value.toFloat(), resources.displayMetrics).toInt()

    private fun requestRuntimePermissions() {
        val needed = mutableListOf<String>()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.POST_NOTIFICATIONS)
                != PackageManager.PERMISSION_GRANTED
            ) {
                needed += Manifest.permission.POST_NOTIFICATIONS
            }
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.NEARBY_WIFI_DEVICES)
                != PackageManager.PERMISSION_GRANTED
            ) {
                needed += Manifest.permission.NEARBY_WIFI_DEVICES
            }
        }
        if (ContextCompat.checkSelfPermission(this, Manifest.permission.ACCESS_FINE_LOCATION)
            != PackageManager.PERMISSION_GRANTED
        ) {
            needed += Manifest.permission.ACCESS_FINE_LOCATION
        }
        if (Build.VERSION.SDK_INT <= Build.VERSION_CODES.P) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.WRITE_EXTERNAL_STORAGE)
                != PackageManager.PERMISSION_GRANTED
            ) {
                needed += Manifest.permission.WRITE_EXTERNAL_STORAGE
            }
        } else if (Build.VERSION.SDK_INT <= Build.VERSION_CODES.S_V2) {
            if (ContextCompat.checkSelfPermission(this, Manifest.permission.READ_EXTERNAL_STORAGE)
                != PackageManager.PERMISSION_GRANTED
            ) {
                needed += Manifest.permission.READ_EXTERNAL_STORAGE
            }
        }
        if (needed.isNotEmpty()) {
            ActivityCompat.requestPermissions(this, needed.toTypedArray(), 10)
        }
    }
}
