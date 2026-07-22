package com.jodu.app.util

import android.app.Notification
import android.app.Person
import android.os.Build
import com.jodu.app.service.IncomingCallMonitor

/**
 * Pulls caller name / number from dialer "incoming call" status-bar notifications.
 * On Android 12+ this is often the only reliable source while ringing.
 */
object CallNotificationParser {
    data class CallerHint(
        val displayName: String?,
        val number: String?,
    )

    fun parse(notification: Notification): CallerHint? {
        val extras = notification.extras ?: return null

        var personName: String? = null
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.P) {
            @Suppress("DEPRECATION")
            val people = extras.getParcelableArrayList<Person>(Notification.EXTRA_PEOPLE_LIST)
            personName = people
                ?.asSequence()
                ?.mapNotNull { it.name?.toString()?.trim()?.takeIf { n -> n.isNotEmpty() } }
                ?.firstOrNull()
            if (personName == null) {
                runCatching {
                    @Suppress("DEPRECATION")
                    extras.getParcelable<Person>("android.messagingPerson")
                }.getOrNull()?.name?.toString()?.trim()?.takeIf { it.isNotEmpty() }?.let {
                    personName = it
                }
            }
        }

        val title = extras.getCharSequence(Notification.EXTRA_TITLE)?.toString()?.trim()
        val text = (
            extras.getCharSequence(Notification.EXTRA_TEXT)
                ?: extras.getCharSequence(Notification.EXTRA_BIG_TEXT)
                ?: extras.getCharSequence(Notification.EXTRA_SUB_TEXT)
                ?: extras.getCharSequence(Notification.EXTRA_INFO_TEXT)
            )?.toString()?.trim()
        val titleBig = extras.getCharSequence(Notification.EXTRA_TITLE_BIG)?.toString()?.trim()

        val candidates = listOfNotNull(personName, titleBig, title, text)
            .map { it.trim() }
            .filter { it.isNotEmpty() }
            .distinct()

        if (candidates.isEmpty()) return null

        var name: String? = IncomingCallMonitor.sanitizeDisplayName(personName)
        var number: String? = null

        for (c in candidates) {
            val sanitizedName = IncomingCallMonitor.sanitizeDisplayName(c)
            val sanitizedNumber = IncomingCallMonitor.sanitizeNumber(c)
            when {
                sanitizedNumber != null && IncomingCallMonitor.looksLikePhoneNumber(c) -> {
                    if (number == null) number = sanitizedNumber
                }
                sanitizedName != null && !IncomingCallMonitor.looksLikePhoneNumber(sanitizedName) -> {
                    if (name == null) name = sanitizedName
                }
            }
        }

        // Common dialer layout: title = name, text = number (or "Mobile").
        if (name == null && !title.isNullOrBlank() && !IncomingCallMonitor.looksLikePhoneNumber(title)) {
            name = IncomingCallMonitor.sanitizeDisplayName(title)
        }
        if (number == null && !text.isNullOrBlank() && IncomingCallMonitor.looksLikePhoneNumber(text)) {
            number = IncomingCallMonitor.sanitizeNumber(text)
        }
        if (number == null && !title.isNullOrBlank() && IncomingCallMonitor.looksLikePhoneNumber(title)) {
            number = IncomingCallMonitor.sanitizeNumber(title)
        }

        if (name == null && number == null) return null
        return CallerHint(displayName = name, number = number)
    }

    fun isLikelyCallNotification(sbnPackage: String, notification: Notification): Boolean {
        if (notification.category == Notification.CATEGORY_CALL) return true
        val extras = notification.extras
        val title = extras?.getCharSequence(Notification.EXTRA_TITLE)?.toString().orEmpty()
        val text = extras?.getCharSequence(Notification.EXTRA_TEXT)?.toString().orEmpty()
        val blob = "$title $text".lowercase()
        if (blob.contains("incoming call") || blob.contains("is calling") || blob.contains("ringing")) {
            return true
        }
        return DIALER_PACKAGES.any { sbnPackage.equals(it, ignoreCase = true) || sbnPackage.startsWith(it) }
            && (notification.flags and Notification.FLAG_ONGOING_EVENT) != 0
    }

    private val DIALER_PACKAGES = listOf(
        "com.google.android.dialer",
        "com.android.dialer",
        "com.samsung.android.dialer",
        "com.samsung.android.incallui",
        "com.android.incallui",
        "com.android.server.telecom",
        "com.miui.phonemanager",
        "com.miui.securitycenter",
        "com.android.phone",
        "com.oneplus.dialer",
        "com.coloros.dialer",
        "com.vivo.dialer",
        "com.huawei.systemmanager",
        "com.android.contacts",
    )
}
