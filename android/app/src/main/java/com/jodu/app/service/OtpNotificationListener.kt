package com.jodu.app.service

import android.app.Notification
import android.content.pm.ApplicationInfo
import android.content.pm.PackageManager
import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import com.jodu.app.protocol.EventTypes
import com.jodu.app.protocol.NotificationPayload
import com.jodu.app.protocol.OtpPayload
import com.jodu.app.util.CallNotificationParser
import com.jodu.app.util.NotificationImageHelper
import com.jodu.app.util.OtpParser

class OtpNotificationListener : NotificationListenerService() {
    override fun onListenerConnected() {
        super.onListenerConnected()
        instance = this
        scanActiveCallNotifications()
    }

    override fun onListenerDisconnected() {
        if (instance === this) instance = null
        super.onListenerDisconnected()
    }

    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        if (sbn == null) return
        if (sbn.packageName == packageName) return
        if (IGNORED_PACKAGES.contains(sbn.packageName)) return

        val notification = sbn.notification ?: return
        if ((notification.flags and Notification.FLAG_GROUP_SUMMARY) != 0) return

        if (handleCallNotification(sbn.packageName, notification)) return

        if (sbn.isOngoing) return

        val extras = notification.extras
        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.trim()
        val text = (
            extras.getCharSequence(Notification.EXTRA_TEXT)
                ?: extras.getCharSequence(Notification.EXTRA_BIG_TEXT)
                ?: extras.getCharSequence(Notification.EXTRA_SUB_TEXT)
            )?.toString()?.trim()

        val thumbnail = NotificationImageHelper.extractThumbnailBase64(this, notification)
        if (title.isNullOrBlank() && text.isNullOrBlank() && thumbnail.isNullOrBlank()) return

        val appName = resolveAppName(sbn.packageName)
        JoduForegroundService.instance?.sendPhoneNotification(
            NotificationPayload(
                packageName = sbn.packageName,
                appName = appName,
                title = title,
                text = text,
                key = sbn.key,
                postedAt = sbn.postTime,
                imageBase64 = thumbnail,
            ),
        )

        val code = OtpParser.extract(title, text) ?: return
        JoduForegroundService.instance?.sendOtp(
            OtpPayload(
                code = code,
                sender = appName ?: sbn.packageName,
                body = listOfNotNull(title, text).joinToString(" — "),
            ),
        )
    }

    private fun handleCallNotification(packageName: String, notification: Notification): Boolean {
        if (!CallNotificationParser.isLikelyCallNotification(packageName, notification)) return false
        val hint = CallNotificationParser.parse(notification) ?: return true
        JoduForegroundService.instance?.onCallNotificationHint(
            displayName = hint.displayName,
            number = hint.number,
        )
        return true
    }

    private fun scanActiveCallNotifications() {
        val active = runCatching { activeNotifications }.getOrNull() ?: return
        for (sbn in active) {
            if (sbn.packageName == packageName) continue
            if (IGNORED_PACKAGES.contains(sbn.packageName)) continue
            val notification = sbn.notification ?: continue
            handleCallNotification(sbn.packageName, notification)
        }
    }

    private fun resolveAppName(packageName: String): String? {
        return try {
            val pm = packageManager
            val info: ApplicationInfo = if (android.os.Build.VERSION.SDK_INT >= android.os.Build.VERSION_CODES.TIRAMISU) {
                pm.getApplicationInfo(
                    packageName,
                    PackageManager.ApplicationInfoFlags.of(0),
                )
            } else {
                @Suppress("DEPRECATION")
                pm.getApplicationInfo(packageName, 0)
            }
            pm.getApplicationLabel(info)?.toString()
        } catch (_: Exception) {
            null
        }
    }

    companion object {
        const val TYPE = EventTypes.OTP_DETECTED

        @Volatile
        private var instance: OtpNotificationListener? = null

        fun requestActiveCallScan() {
            instance?.scanActiveCallNotifications()
        }

        private val IGNORED_PACKAGES = setOf(
            "android",
            "com.android.systemui",
            "com.android.providers.downloads",
            "com.google.android.gms",
            "com.google.android.gsf",
        )
    }
}
