package com.jodu.app.protocol

import kotlinx.serialization.SerialName
import kotlinx.serialization.Serializable
import kotlinx.serialization.encodeToString
import kotlinx.serialization.json.Json
import kotlinx.serialization.json.JsonElement
import kotlinx.serialization.json.decodeFromJsonElement
import kotlinx.serialization.json.encodeToJsonElement

@Serializable
data class JoduMessage(
    val type: String,
    val payload: JsonElement? = null,
)

@Serializable
data class TelemetryPayload(
    @SerialName("batteryPercent") val batteryPercent: Int,
    @SerialName("isCharging") val isCharging: Boolean,
    @SerialName("wifiSsid") val wifiSsid: String? = null,
    @SerialName("wifiConnected") val wifiConnected: Boolean = false,
    @SerialName("wifiRssi") val wifiRssi: Int? = null,
    @SerialName("deviceName") val deviceName: String? = null,
)

@Serializable
data class ClipboardPayload(
    val text: String,
    val source: String = "phone",
)

@Serializable
data class OtpPayload(
    val code: String,
    val sender: String? = null,
    val body: String? = null,
)

@Serializable
data class NotificationPayload(
    @SerialName("packageName") val packageName: String,
    @SerialName("appName") val appName: String? = null,
    val title: String? = null,
    val text: String? = null,
    val key: String? = null,
    @SerialName("postedAt") val postedAt: Long = 0L,
    /** JPEG thumbnail, base64 (no data: prefix). */
    @SerialName("imageBase64") val imageBase64: String? = null,
)

@Serializable
data class MediaControlPayload(
    val action: String,
)

@Serializable
data class MediaStatePayload(
    val title: String? = null,
    val artist: String? = null,
    @SerialName("isPlaying") val isPlaying: Boolean = false,
    val volume: Int = 0,
)

@Serializable
data class DiscoveryPayload(
    @SerialName("deviceId") val deviceId: String,
    @SerialName("deviceName") val deviceName: String,
    val role: String,
    val ip: String,
    @SerialName("wsPort") val wsPort: Int = JoduPorts.WEB_SOCKET,
    @SerialName("httpPort") val httpPort: Int = JoduPorts.FILE_HTTP,
)

@Serializable
data class PairPayload(
    @SerialName("fromDeviceId") val fromDeviceId: String,
    @SerialName("fromDeviceName") val fromDeviceName: String,
    @SerialName("fromRole") val fromRole: String,
    @SerialName("fromIp") val fromIp: String,
    @SerialName("wsPort") val wsPort: Int = JoduPorts.WEB_SOCKET,
    @SerialName("httpPort") val httpPort: Int = JoduPorts.FILE_HTTP,
    @SerialName("targetDeviceId") val targetDeviceId: String,
    val accepted: Boolean? = null,
)

@Serializable
data class FileTransferPayload(
    @SerialName("fileName") val fileName: String,
    val direction: String = "send",
    @SerialName("bytesTransferred") val bytesTransferred: Long = 0L,
    @SerialName("totalBytes") val totalBytes: Long = 0L,
    val percent: Int = 0,
    val status: String = "progress",
    val error: String? = null,
)

object JoduJson {
    val json = Json {
        ignoreUnknownKeys = true
        encodeDefaults = true
        explicitNulls = false
        isLenient = true
    }

    inline fun <reified T> message(type: String, payload: T?): String {
        val element = payload?.let { json.encodeToJsonElement(it) }
        return json.encodeToString(JoduMessage(type, element))
    }

    fun parse(raw: String): JoduMessage = json.decodeFromString(raw)

    inline fun <reified T> payload(message: JoduMessage): T? {
        val element = message.payload ?: return null
        return json.decodeFromJsonElement(element)
    }
}
