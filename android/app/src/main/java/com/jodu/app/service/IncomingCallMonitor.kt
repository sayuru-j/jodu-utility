package com.jodu.app.service

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.os.Handler
import android.os.Looper
import android.provider.CallLog
import android.provider.ContactsContract
import android.telecom.TelecomManager
import android.telephony.PhoneNumberUtils
import android.telephony.PhoneStateListener
import android.telephony.TelephonyCallback
import android.telephony.TelephonyManager
import androidx.core.content.ContextCompat
import com.jodu.app.protocol.IncomingCallPayload
import java.util.UUID
import java.util.concurrent.Executor

/**
 * Monitors cellular call state and answers / declines via TelecomManager.
 * Caller name/number often come from dialer notifications (Android 12+ hides the number
 * from TelephonyCallback) and are enriched via [applyCallerHint].
 */
class IncomingCallMonitor(
    private val context: Context,
    private val onCallEvent: (IncomingCallPayload) -> Unit,
) {
    private val telephony = context.getSystemService(TelephonyManager::class.java)
    private val telecom = context.getSystemService(TelecomManager::class.java)
    private val mainHandler = Handler(Looper.getMainLooper())

    private var callbackApi31: TelephonyCallback? = null
    @Suppress("DEPRECATION")
    private var legacyListener: PhoneStateListener? = null

    @Volatile
    private var activeCallId: String? = null

    @Volatile
    private var lastNumber: String? = null

    @Volatile
    private var lastDisplayName: String? = null

    @Volatile
    private var wasRinging = false

    private var enrichAttempt = 0

    private val enrichRunnable = Runnable { attemptEnrichFromLocalSources() }

    fun start() {
        if (telephony == null) return
        if (!hasPhoneStatePermission()) return
        stop()

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val cb = object : TelephonyCallback(), TelephonyCallback.CallStateListener {
                override fun onCallStateChanged(state: Int) {
                    handleState(state, incomingNumber = null)
                }
            }
            callbackApi31 = cb
            val executor: Executor = ContextCompat.getMainExecutor(context)
            telephony.registerTelephonyCallback(executor, cb)
        } else {
            @Suppress("DEPRECATION")
            val listener = object : PhoneStateListener() {
                @Deprecated("Deprecated in Java")
                override fun onCallStateChanged(state: Int, phoneNumber: String?) {
                    handleState(state, phoneNumber)
                }
            }
            legacyListener = listener
            @Suppress("DEPRECATION")
            telephony.listen(listener, PhoneStateListener.LISTEN_CALL_STATE)
        }
    }

    fun stop() {
        cancelEnrich()
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            callbackApi31?.let { runCatching { telephony?.unregisterTelephonyCallback(it) } }
            callbackApi31 = null
        } else {
            @Suppress("DEPRECATION")
            legacyListener?.let { runCatching { telephony?.listen(it, PhoneStateListener.LISTEN_NONE) } }
            legacyListener = null
        }
    }

    /**
     * Dialer call notifications usually carry the contact name (title) and/or number.
     * Prefer this over Telephony on API 31+.
     */
    fun applyCallerHint(displayName: String?, number: String?) {
        val name = sanitizeDisplayName(displayName)
        val num = sanitizeNumber(number)
        if (name.isNullOrBlank() && num.isNullOrBlank()) return

        var changed = false
        if (!num.isNullOrBlank() && !numbersMatch(lastNumber, num)) {
            lastNumber = num
            changed = true
            if (lastDisplayName.isNullOrBlank() || looksLikePhoneNumber(lastDisplayName!!)) {
                resolveDisplayName(num)?.let {
                    lastDisplayName = it
                    changed = true
                }
            }
        }
        if (!name.isNullOrBlank() && !looksLikePhoneNumber(name)) {
            if (!name.equals(lastDisplayName, ignoreCase = true)) {
                lastDisplayName = name
                changed = true
            }
        } else if (!name.isNullOrBlank() && lastNumber.isNullOrBlank()) {
            lastNumber = sanitizeNumber(name) ?: name
            changed = true
        }

        if (changed && wasRinging) {
            emitRinging()
        }
    }

    fun answer() {
        if (!hasAnswerPermission()) return
        runCatching {
            @Suppress("DEPRECATION")
            telecom?.acceptRingingCall()
        }
    }

    fun decline() {
        if (!hasAnswerPermission()) return
        runCatching {
            @Suppress("DEPRECATION")
            telecom?.endCall()
        }
        val id = activeCallId
        if (wasRinging) {
            emit(
                IncomingCallPayload(
                    state = "rejected",
                    number = lastNumber,
                    displayName = bestDisplayName(),
                    callId = id,
                ),
            )
        }
        clearCall()
    }

    private fun handleState(state: Int, incomingNumber: String?) {
        when (state) {
            TelephonyManager.CALL_STATE_RINGING -> {
                val number = resolveIncomingNumber(incomingNumber)
                if (!number.isNullOrBlank()) lastNumber = number
                resolveDisplayName(lastNumber)?.let { lastDisplayName = it }
                if (lastDisplayName.isNullOrBlank() || looksLikePhoneNumber(lastDisplayName!!)) {
                    latestCallerFromCallLog()?.let { (num, cachedName) ->
                        if (!num.isNullOrBlank()) lastNumber = num
                        if (!cachedName.isNullOrBlank() && !looksLikePhoneNumber(cachedName)) {
                            lastDisplayName = cachedName
                        }
                    }
                }

                if (!wasRinging) {
                    activeCallId = UUID.randomUUID().toString().take(12)
                    wasRinging = true
                    emitRinging()
                    scheduleEnrich()
                    // Notification may have posted before Telephony RINGING — ask listener to re-scan.
                    JoduForegroundService.instance?.requestActiveCallNotificationScan()
                } else {
                    emitRinging()
                }
            }

            TelephonyManager.CALL_STATE_OFFHOOK -> {
                if (wasRinging) {
                    emit(
                        IncomingCallPayload(
                            state = "answered",
                            number = lastNumber,
                            displayName = bestDisplayName(),
                            callId = activeCallId,
                        ),
                    )
                }
                wasRinging = false
                cancelEnrich()
            }

            TelephonyManager.CALL_STATE_IDLE -> {
                if (wasRinging || activeCallId != null) {
                    emit(
                        IncomingCallPayload(
                            state = "ended",
                            number = lastNumber,
                            displayName = bestDisplayName(),
                            callId = activeCallId,
                        ),
                    )
                }
                clearCall()
            }
        }
    }

    private fun emitRinging() {
        emit(
            IncomingCallPayload(
                state = "ringing",
                number = lastNumber,
                displayName = bestDisplayName(),
                callId = activeCallId,
            ),
        )
    }

    private fun bestDisplayName(): String? =
        lastDisplayName?.takeIf { it.isNotBlank() && !looksLikePhoneNumber(it) }
            ?: lastDisplayName
            ?: lastNumber

    private fun clearCall() {
        cancelEnrich()
        wasRinging = false
        activeCallId = null
        lastNumber = null
        lastDisplayName = null
    }

    private fun scheduleEnrich() {
        cancelEnrich()
        enrichAttempt = 0
        mainHandler.postDelayed(enrichRunnable, ENRICH_DELAYS_MS[0])
    }

    private fun cancelEnrich() {
        mainHandler.removeCallbacks(enrichRunnable)
        enrichAttempt = 0
    }

    private fun attemptEnrichFromLocalSources() {
        if (!wasRinging) return
        val beforeNumber = lastNumber
        val beforeName = lastDisplayName

        latestCallerFromCallLog()?.let { (num, cachedName) ->
            if (!num.isNullOrBlank()) lastNumber = num
            if (!cachedName.isNullOrBlank() && !looksLikePhoneNumber(cachedName)) {
                lastDisplayName = cachedName
            }
        }
        resolveDisplayName(lastNumber)?.let { lastDisplayName = it }

        if (lastNumber != beforeNumber || lastDisplayName != beforeName) {
            emitRinging()
        }

        enrichAttempt++
        if (enrichAttempt < ENRICH_DELAYS_MS.size && wasRinging) {
            mainHandler.postDelayed(enrichRunnable, ENRICH_DELAYS_MS[enrichAttempt])
        }
    }

    private fun resolveIncomingNumber(incomingNumber: String?): String? {
        val fromCallback = sanitizeNumber(incomingNumber)
        if (fromCallback != null) return fromCallback
        lastNumber?.trim()?.takeIf { it.isNotEmpty() }?.let { return it }
        return latestCallerFromCallLog()?.first
    }

    private fun latestCallerFromCallLog(): Pair<String?, String?>? {
        if (!hasCallLogPermission()) return null
        return runCatching {
            context.contentResolver.query(
                CallLog.Calls.CONTENT_URI,
                arrayOf(
                    CallLog.Calls.NUMBER,
                    CallLog.Calls.CACHED_NAME,
                    CallLog.Calls.TYPE,
                    CallLog.Calls.DATE,
                ),
                null,
                null,
                "${CallLog.Calls.DATE} DESC",
            )?.use { cursor ->
                val numberIdx = cursor.getColumnIndex(CallLog.Calls.NUMBER)
                val nameIdx = cursor.getColumnIndex(CallLog.Calls.CACHED_NAME)
                val typeIdx = cursor.getColumnIndex(CallLog.Calls.TYPE)
                val dateIdx = cursor.getColumnIndex(CallLog.Calls.DATE)
                if (numberIdx < 0 || typeIdx < 0) return@use null
                val now = System.currentTimeMillis()
                while (cursor.moveToNext()) {
                    val type = cursor.getInt(typeIdx)
                    if (dateIdx >= 0) {
                        val age = now - cursor.getLong(dateIdx)
                        if (age > 120_000L) break
                        val isIncomingish =
                            type == CallLog.Calls.INCOMING_TYPE ||
                                type == CallLog.Calls.MISSED_TYPE ||
                                type == CallLog.Calls.REJECTED_TYPE
                        if (!isIncomingish && age > 15_000L) continue
                    } else {
                        val isIncomingish =
                            type == CallLog.Calls.INCOMING_TYPE ||
                                type == CallLog.Calls.MISSED_TYPE ||
                                type == CallLog.Calls.REJECTED_TYPE
                        if (!isIncomingish) continue
                    }
                    val number = cursor.getString(numberIdx)?.trim()?.takeIf { it.isNotEmpty() }
                    val cached = if (nameIdx >= 0) {
                        cursor.getString(nameIdx)?.trim()?.takeIf { it.isNotEmpty() }
                    } else {
                        null
                    }
                    if (number != null || cached != null) return@use number to cached
                }
                null
            }
        }.getOrNull()
    }

    private fun resolveDisplayName(number: String?): String? {
        if (number.isNullOrBlank()) return null
        if (!hasContactsPermission()) return null
        return runCatching {
            val uri = Uri.withAppendedPath(
                ContactsContract.PhoneLookup.CONTENT_FILTER_URI,
                Uri.encode(number),
            )
            context.contentResolver.query(
                uri,
                arrayOf(ContactsContract.PhoneLookup.DISPLAY_NAME),
                null,
                null,
                null,
            )?.use { cursor ->
                if (!cursor.moveToFirst()) return@use null
                val idx = cursor.getColumnIndex(ContactsContract.PhoneLookup.DISPLAY_NAME)
                if (idx < 0) return@use null
                cursor.getString(idx)?.trim()?.takeIf { it.isNotEmpty() }
            }
        }.getOrNull()
    }

    private fun emit(payload: IncomingCallPayload) {
        onCallEvent(payload)
    }

    private fun hasPhoneStatePermission(): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.READ_PHONE_STATE) ==
            PackageManager.PERMISSION_GRANTED

    private fun hasAnswerPermission(): Boolean {
        if (Build.VERSION.SDK_INT < Build.VERSION_CODES.O) return true
        return ContextCompat.checkSelfPermission(context, Manifest.permission.ANSWER_PHONE_CALLS) ==
            PackageManager.PERMISSION_GRANTED
    }

    private fun hasCallLogPermission(): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.READ_CALL_LOG) ==
            PackageManager.PERMISSION_GRANTED

    private fun hasContactsPermission(): Boolean =
        ContextCompat.checkSelfPermission(context, Manifest.permission.READ_CONTACTS) ==
            PackageManager.PERMISSION_GRANTED

    companion object {
        private val ENRICH_DELAYS_MS = longArrayOf(300, 800, 1600, 3000)

        fun looksLikePhoneNumber(value: String): Boolean {
            val trimmed = value.trim()
            if (trimmed.isEmpty()) return false
            val digits = trimmed.count { it.isDigit() }
            if (digits < 7) return false
            val significant = trimmed.count { !it.isWhitespace() }
            return digits.toFloat() / significant >= 0.55f
        }

        fun sanitizeNumber(raw: String?): String? {
            val value = raw?.trim()?.takeIf { it.isNotEmpty() } ?: return null
            return value.takeIf { looksLikePhoneNumber(it) || PhoneNumberUtils.isGlobalPhoneNumber(it) }
                ?: value.takeIf { it.any { ch -> ch.isDigit() } && it.length in 7..32 }
        }

        fun sanitizeDisplayName(raw: String?): String? {
            val value = raw?.trim()?.takeIf { it.isNotEmpty() } ?: return null
            val lower = value.lowercase()
            if (lower in IGNORED_TITLES) return null
            if (lower.startsWith("incoming") || lower.startsWith("ringing")) return null
            return value
        }

        fun numbersMatch(a: String?, b: String?): Boolean {
            if (a.isNullOrBlank() || b.isNullOrBlank()) return false
            if (a.equals(b, ignoreCase = true)) return true
            val na = PhoneNumberUtils.normalizeNumber(a) ?: a.filter { it.isDigit() }
            val nb = PhoneNumberUtils.normalizeNumber(b) ?: b.filter { it.isDigit() }
            if (na.isEmpty() || nb.isEmpty()) return false
            return na == nb || na.endsWith(nb) || nb.endsWith(na)
        }

        private val IGNORED_TITLES = setOf(
            "incoming call",
            "ringing",
            "phone",
            "call",
            "mobile",
            "unknown",
            "unknown caller",
            "private number",
            "restricted",
            "no caller id",
        )
    }
}
