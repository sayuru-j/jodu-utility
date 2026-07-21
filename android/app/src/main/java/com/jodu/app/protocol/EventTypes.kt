package com.jodu.app.protocol

object EventTypes {
    const val CLIPBOARD_UPDATE = "CLIPBOARD_UPDATE"
    const val TELEMETRY = "TELEMETRY"
    const val OTP_DETECTED = "OTP_DETECTED"
    const val MEDIA_CONTROL = "MEDIA_CONTROL"
    const val MEDIA_STATE = "MEDIA_STATE"
    const val PING_DEVICE = "PING_DEVICE"
    const val DISCOVERY = "DISCOVERY"
    const val PAIR_REQUEST = "PAIR_REQUEST"
    const val PAIR_RESPONSE = "PAIR_RESPONSE"
}

object MediaActions {
    const val PLAY = "PLAY"
    const val PAUSE = "PAUSE"
    const val NEXT = "NEXT"
    const val PREVIOUS = "PREVIOUS"
    const val VOLUME_UP = "VOLUME_UP"
    const val VOLUME_DOWN = "VOLUME_DOWN"
}

object JoduPorts {
    const val DISCOVERY = 19283
    const val WEB_SOCKET = 19284
    const val FILE_HTTP = 19286
}
