package com.jodu.app.service

import android.Manifest
import android.content.Context
import android.content.pm.PackageManager
import android.net.Uri
import android.os.Build
import android.provider.CallLog
import android.provider.ContactsContract
import android.telecom.TelecomManager
import android.telephony.PhoneStateListener
import android.telephony.TelephonyCallback
import android.telephony.TelephonyManager
import androidx.core.content.ContextCompat
import com.jodu.app.protocol.IncomingCallPayload
import java.util.UUID
import java.util.concurrent.Executor

/**
 * Monitors cellular call state and answers / declines via TelecomManager.
 */
class IncomingCallMonitor(
    private val context: Context,
    private val onCallEvent: (IncomingCallPayload) -> Unit,
) {
    private val telephony = context.getSystemService(TelephonyManager::class.java)
    private val telecom = context.getSystemService(TelecomManager::class.java)

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
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            callbackApi31?.let { runCatching { telephony?.unregisterTelephonyCallback(it) } }
            callbackApi31 = null
        } else {
            @Suppress("DEPRECATION")
            legacyListener?.let { runCatching { telephony?.listen(it, PhoneStateListener.LISTEN_NONE) } }
            legacyListener = null
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
                    displayName = lastDisplayName ?: lastNumber,
                    callId = id,
                ),
            )
        }
        wasRinging = false
        activeCallId = null
    }

    private fun handleState(state: Int, incomingNumber: String?) {
        when (state) {
            TelephonyManager.CALL_STATE_RINGING -> {
                val number = resolveIncomingNumber(incomingNumber)
                val name = resolveDisplayName(number)
                if (!number.isNullOrBlank()) lastNumber = number
                if (!name.isNullOrBlank()) lastDisplayName = name

                if (!wasRinging) {
                    activeCallId = UUID.randomUUID().toString().take(12)
                    wasRinging = true
                    emit(
                        IncomingCallPayload(
                            state = "ringing",
                            number = lastNumber,
                            displayName = lastDisplayName ?: lastNumber,
                            callId = activeCallId,
                        ),
                    )
                } else if (!lastNumber.isNullOrBlank() || !lastDisplayName.isNullOrBlank()) {
                    emit(
                        IncomingCallPayload(
                            state = "ringing",
                            number = lastNumber,
                            displayName = lastDisplayName ?: lastNumber,
                            callId = activeCallId,
                        ),
                    )
                }
            }

            TelephonyManager.CALL_STATE_OFFHOOK -> {
                if (wasRinging) {
                    emit(
                        IncomingCallPayload(
                            state = "answered",
                            number = lastNumber,
                            displayName = lastDisplayName ?: lastNumber,
                            callId = activeCallId,
                        ),
                    )
                }
                wasRinging = false
            }

            TelephonyManager.CALL_STATE_IDLE -> {
                if (wasRinging || activeCallId != null) {
                    emit(
                        IncomingCallPayload(
                            state = "ended",
                            number = lastNumber,
                            displayName = lastDisplayName ?: lastNumber,
                            callId = activeCallId,
                        ),
                    )
                }
                wasRinging = false
                activeCallId = null
                lastNumber = null
                lastDisplayName = null
            }
        }
    }

    private fun resolveIncomingNumber(incomingNumber: String?): String? {
        val fromCallback = incomingNumber?.trim()?.takeIf { it.isNotEmpty() }
        if (fromCallback != null) return fromCallback
        lastNumber?.trim()?.takeIf { it.isNotEmpty() }?.let { return it }
        return latestIncomingNumberFromCallLog()
    }

    private fun latestIncomingNumberFromCallLog(): String? {
        if (!hasCallLogPermission()) return null
        return runCatching {
            context.contentResolver.query(
                CallLog.Calls.CONTENT_URI,
                arrayOf(CallLog.Calls.NUMBER, CallLog.Calls.TYPE, CallLog.Calls.DATE),
                null,
                null,
                "${CallLog.Calls.DATE} DESC",
            )?.use { cursor ->
                val numberIdx = cursor.getColumnIndex(CallLog.Calls.NUMBER)
                val typeIdx = cursor.getColumnIndex(CallLog.Calls.TYPE)
                val dateIdx = cursor.getColumnIndex(CallLog.Calls.DATE)
                if (numberIdx < 0 || typeIdx < 0) return@use null
                val now = System.currentTimeMillis()
                while (cursor.moveToNext()) {
                    val type = cursor.getInt(typeIdx)
                    if (type != CallLog.Calls.INCOMING_TYPE && type != CallLog.Calls.MISSED_TYPE) {
                        continue
                    }
                    if (dateIdx >= 0) {
                        val age = now - cursor.getLong(dateIdx)
                        if (age > 60_000L) break
                    }
                    return@use cursor.getString(numberIdx)?.trim()?.takeIf { it.isNotEmpty() }
                }
                null
            }
        }.getOrNull()
    }

    private fun resolveDisplayName(number: String?): String? {
        if (number.isNullOrBlank()) return lastDisplayName
        if (!hasContactsPermission()) return lastDisplayName
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
        }.getOrNull() ?: lastDisplayName
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
}
