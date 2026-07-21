package com.jodu.app

import android.Manifest
import android.content.Intent
import android.content.pm.PackageManager
import android.os.Build
import android.os.Bundle
import android.os.Handler
import android.os.Looper
import android.provider.Settings
import android.view.View
import android.widget.Button
import android.widget.TextView
import androidx.appcompat.app.AppCompatActivity
import androidx.core.app.ActivityCompat
import androidx.core.content.ContextCompat
import com.jodu.app.service.JoduForegroundService

class MainActivity : AppCompatActivity() {
    private lateinit var status: TextView
    private lateinit var linkLabel: TextView
    private lateinit var linkGlyph: View
    private val uiHandler = Handler(Looper.getMainLooper())
    private val refreshTick = object : Runnable {
        override fun run() {
            refreshStatus()
            uiHandler.postDelayed(this, 2000)
        }
    }

    override fun onCreate(savedInstanceState: Bundle?) {
        super.onCreate(savedInstanceState)
        setContentView(R.layout.activity_main)

        status = findViewById(R.id.status)
        linkLabel = findViewById(R.id.linkLabel)
        linkGlyph = findViewById(R.id.linkGlyph)

        findViewById<Button>(R.id.btnStart).setOnClickListener {
            requestRuntimePermissions()
            startBridge()
        }
        findViewById<Button>(R.id.btnNotifications).setOnClickListener {
            startActivity(Intent(Settings.ACTION_NOTIFICATION_LISTENER_SETTINGS))
        }

        requestRuntimePermissions()
        startBridge()
        refreshStatus()
    }

    override fun onResume() {
        super.onResume()
        refreshStatus()
        uiHandler.post(refreshTick)
    }

    override fun onPause() {
        uiHandler.removeCallbacks(refreshTick)
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
        if (linked) {
            status.setText(R.string.status_linked)
            linkLabel.setText(R.string.service_running)
            linkLabel.setTextColor(ContextCompat.getColor(this, R.color.fg))
            linkGlyph.setBackgroundResource(R.drawable.bg_glyph_on)
        } else {
            if (service != null) {
                status.setText(R.string.status_running)
            }
            linkLabel.setText(R.string.service_waiting)
            linkLabel.setTextColor(ContextCompat.getColor(this, R.color.muted))
            linkGlyph.setBackgroundResource(R.drawable.bg_glyph)
        }
    }

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
