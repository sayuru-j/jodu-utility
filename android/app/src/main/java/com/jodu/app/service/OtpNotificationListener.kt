package com.jodu.app.service

import android.service.notification.NotificationListenerService
import android.service.notification.StatusBarNotification
import com.jodu.app.protocol.EventTypes
import com.jodu.app.protocol.OtpPayload
import com.jodu.app.util.OtpParser

class OtpNotificationListener : NotificationListenerService() {
    override fun onNotificationPosted(sbn: StatusBarNotification?) {
        if (sbn == null) return
        val extras = sbn.notification.extras
        val title = extras.getCharSequence("android.title")?.toString()
        val text = extras.getCharSequence("android.text")?.toString()
            ?: extras.getCharSequence("android.bigText")?.toString()

        val code = OtpParser.extract(title, text) ?: return
        val sender = sbn.packageName
        JoduForegroundService.instance?.sendOtp(
            OtpPayload(code = code, sender = sender, body = listOfNotNull(title, text).joinToString(" — ")),
        )
    }

    companion object {
        // Event type re-export for clarity at call sites if needed
        const val TYPE = EventTypes.OTP_DETECTED
    }
}
