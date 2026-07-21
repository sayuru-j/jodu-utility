package com.jodu.app

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.graphics.Typeface
import android.os.Build
import android.os.Bundle
import android.provider.Settings
import android.util.TypedValue
import android.view.Gravity
import android.view.View
import android.widget.Button
import android.widget.LinearLayout
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.jodu.app.protocol.DiscoveryPayload
import com.jodu.app.service.JoduForegroundService

class MainActivity : AppCompatActivity() {
    private lateinit var status: TextView
    private lateinit var linkLabel: TextView
    private lateinit var linkGlyph: View
    private lateinit var pairBanner: View
    private lateinit var pairFrom: TextView
    private lateinit var devicesEmpty: TextView
    private lateinit var deviceList: LinearLayout

    private val uiListener = { runOnUiThread { refreshStatus() } }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        status = findViewById(R.id.status)
        linkLabel = findViewById(R.id.linkLabel)
        linkGlyph = findViewById(R.id.linkGlyph)
        pairBanner = findViewById(R.id.pairBanner)
        pairFrom = findViewById(R.id.pairFrom)
        devicesEmpty = findViewById(R.id.devicesEmpty)
        deviceList = findViewById(R.id.deviceList)

        findViewById<Button>(R.id.btnStart).setOnClickListener {
            requestRuntimePermissions()
            startBridge()
        }
        findViewById<Button>(R.id.btnNotifications).setOnClickListener {
            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }
        findViewById<Button>(R.id.btnAccept).setOnClickListener {
            JoduForegroundService.instance?.acceptPair()
        }
        findViewById<Button>(R.id.btnDecline).setOnClickListener {
            JoduForegroundService.instance?.rejectPair()
        }

        requestRuntimePermissions()
        startBridge()
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
        val intent = Intent(this, JoduForegroundService::class.java)
        ContextCompat.startForegroundService(this, intent)
        status.setText(R.string.status_running)
        refreshStatus()
    }

    private fun refreshStatus() {
        val service = JoduForegroundService.instance
        val linked = service?.isLinked == true
        val incoming = service?.incomingPair
        val peers = service?.lanPeers.orEmpty()

        when {
            linked -> status.setText(R.string.status_linked)
            incoming != null -> status.setText(R.string.status_incoming)
            service?.pairStatus == "outgoing" -> status.setText(R.string.status_outgoing)
            service != null -> status.setText(R.string.status_running)
            else -> status.setText(R.string.status_starting)
        }

        if (linked) {
            linkLabel.setText(R.string.service_running)
            linkLabel.setTextColor(ContextCompat.getColor(this, R.color.fg))
            linkGlyph.setBackgroundResource(R.drawable.bg_glyph_on)
        } else {
            linkLabel.setText(R.string.service_waiting)
            linkLabel.setTextColor(ContextCompat.getColor(this, R.color.muted))
            linkGlyph.setBackgroundResource(R.drawable.bg_glyph)
        }

        if (incoming != null) {
            pairBanner.visibility = View.VISIBLE
            pairFrom.text = "${incoming.fromDeviceName}\n${incoming.fromIp}"
        } else {
            pairBanner.visibility = View.GONE
        }

        renderDevices(peers, service)
    }

    private fun renderDevices(peers: List<DiscoveryPayload>, service: JoduForegroundService?) {
        deviceList.removeAllViews()
        if (peers.isEmpty()) {
            devicesEmpty.visibility = View.VISIBLE
            return
        }
        devicesEmpty.visibility = View.GONE

        val linked = service?.isLinked == true
        val outgoing = service?.outgoingPairDeviceId
        val pairedId = service?.pairedDesktop?.deviceId
        val busy = linked || service?.pairStatus == "outgoing"

        peers.forEach { peer ->
            val row = LinearLayout(this).apply {
                orientation = LinearLayout.HORIZONTAL
                gravity = Gravity.CENTER_VERTICAL
                setPadding(dp(12), dp(12), dp(12), dp(12))
                background = ContextCompat.getDrawable(this@MainActivity, R.drawable.bg_cell)
                layoutParams = LinearLayout.LayoutParams(
                    LinearLayout.LayoutParams.MATCH_PARENT,
                    LinearLayout.LayoutParams.WRAP_CONTENT,
                ).apply { bottomMargin = dp(8) }
                isEnabled = !busy
                alpha = if (busy && peer.deviceId != pairedId && peer.deviceId != outgoing) 0.45f else 1f
                setOnClickListener {
                    if (!busy) service?.requestPair(peer.deviceId)
                }
            }

            val dot = View(this).apply {
                layoutParams = LinearLayout.LayoutParams(dp(7), dp(7)).apply { marginEnd = dp(10) }
                setBackgroundResource(
                    if (peer.deviceId == pairedId) R.drawable.bg_glyph_on else R.drawable.bg_glyph,
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
                    peer.deviceId == pairedId -> "linked"
                    peer.deviceId == outgoing -> "…"
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
        }
        if (needed.isNotEmpty()) {
            ActivityCompat.requestPermissions(this, needed.toTypedArray(), 10)
        }
    }
}
