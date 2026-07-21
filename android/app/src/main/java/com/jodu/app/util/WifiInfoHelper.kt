package com.jodu.app.util

import android.annotation.SuppressLint
import android.content.Context
import android.content.pm.PackageManager
import android.net.ConnectivityManager
import android.net.NetworkCapabilities
import android.net.wifi.WifiInfo
import android.net.wifi.WifiManager
import android.os.Build
import androidx.core.content.ContextCompat

object WifiInfoHelper {
    data class Snapshot(
        val ssid: String?,
        val connected: Boolean,
        val rssi: Int?,
    )

    @SuppressLint("MissingPermission")
    fun snapshot(context: Context): Snapshot {
        val app = context.applicationContext
        val cm = app.getSystemService(Context.CONNECTIVITY_SERVICE) as ConnectivityManager
        val network = cm.activeNetwork
        val caps = network?.let { cm.getNetworkCapabilities(it) }
        val onWifi = caps?.hasTransport(NetworkCapabilities.TRANSPORT_WIFI) == true

        var ssid: String? = null
        var rssi: Int? = null

        if (onWifi && Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val info = caps?.transportInfo as? WifiInfo
            ssid = cleanSsid(info?.ssid)
            rssi = info?.rssi?.takeIf { it != Int.MIN_VALUE }
        }

        if (ssid == null || rssi == null) {
            @Suppress("DEPRECATION")
            val legacy = (app.getSystemService(Context.WIFI_SERVICE) as WifiManager).connectionInfo
            if (ssid == null) ssid = cleanSsid(legacy?.ssid)
            if (rssi == null) rssi = legacy?.rssi?.takeIf { it != Int.MIN_VALUE }
        }

        // Without location / nearby-wifi permission Android returns <unknown ssid>.
        if (ssid == null && onWifi && !hasWifiIdentityPermission(app)) {
            ssid = null // UI will fall back to "Wi‑Fi" via wifiConnected
        }

        return Snapshot(
            ssid = ssid,
            connected = onWifi,
            rssi = rssi,
        )
    }

    private fun cleanSsid(raw: String?): String? {
        val value = raw?.trim()?.trim('"')?.takeIf { it.isNotBlank() } ?: return null
        if (value.equals("<unknown ssid>", ignoreCase = true)) return null
        if (value == "0x") return null
        return value
    }

    private fun hasWifiIdentityPermission(context: Context): Boolean {
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            if (ContextCompat.checkSelfPermission(
                    context,
                    android.Manifest.permission.NEARBY_WIFI_DEVICES,
                ) == PackageManager.PERMISSION_GRANTED
            ) {
                return true
            }
        }
        return ContextCompat.checkSelfPermission(
            context,
            android.Manifest.permission.ACCESS_FINE_LOCATION,
        ) == PackageManager.PERMISSION_GRANTED
    }
}
