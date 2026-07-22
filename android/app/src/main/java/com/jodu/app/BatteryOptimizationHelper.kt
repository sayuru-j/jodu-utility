package com.jodu.app

import android.app.Activity
import android.content.Context
import android.content.Intent
import android.net.Uri
import android.os.Build
import android.os.PowerManager
import android.provider.Settings

object BatteryOptimizationHelper {
    fun isIgnoringBatteryOptimizations(context: Context): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) return true
        val pm = context.getSystemService(PowerManager::class.java) ?: return true
        return pm.isIgnoringBatteryOptimizations(context.packageName)
    }

    fun requestUnrestrictedBackground(activity: Activity) {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.M) {
            openAppDetails(activity)
            return
        }

        if (isIgnoringBatteryOptimizations(activity)) {
            openAppDetails(activity)
            return
        }

        runCatching {
            activity.startActivity(
                Intent(Settings.ACTION_REQUEST_IGNORE_BATTERY_OPTIMIZATIONS).apply {
                    data = Uri.parse("package:${activity.packageName}")
                },
            )
        }.onFailure {
            openBatterySettings(activity)
        }
    }

    fun openBatterySettings(context: Context) {
        runCatching {
            context.startActivity(
                Intent(Settings.ACTION_IGNORE_BATTERY_OPTIMIZATION_SETTINGS)
                    .addFlags(Intent.FLAG_ACTIVITY_NEW_TASK),
            )
        }.onFailure {
            openAppDetails(context)
        }
    }

    fun openAppDetails(context: Context) {
        runCatching {
            context.startActivity(
                Intent(Settings.ACTION_APPLICATION_DETAILS_SETTINGS).apply {
                    data = Uri.fromParts("package", context.packageName, null)
                    addFlags(Intent.FLAG_ACTIVITY_NEW_TASK)
                },
            )
        }
    }
}
