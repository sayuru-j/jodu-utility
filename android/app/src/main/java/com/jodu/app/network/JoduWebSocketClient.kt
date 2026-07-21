package com.jodu.app.network

import com.jodu.app.protocol.JoduJson
import com.jodu.app.protocol.JoduMessage
import kotlinx.coroutines.CoroutineScope
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.Job
import kotlinx.coroutines.delay
import kotlinx.coroutines.isActive
import kotlinx.coroutines.launch
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.Response
import okhttp3.WebSocket
import okhttp3.WebSocketListener
import okio.ByteString
import java.util.concurrent.TimeUnit
import java.util.concurrent.atomic.AtomicBoolean

class JoduWebSocketClient(
    private val scope: CoroutineScope,
    private val onMessage: (JoduMessage) -> Unit,
    private val onConnectionChanged: (Boolean) -> Unit,
) {
    private val client = OkHttpClient.Builder()
        .readTimeout(0, TimeUnit.MILLISECONDS)
        .pingInterval(20, TimeUnit.SECONDS)
        .build()

    private var socket: WebSocket? = null
    private var reconnectJob: Job? = null
    private var endpoint: String? = null
    private val intentionalClose = AtomicBoolean(false)

    @Volatile
    var isConnected: Boolean = false
        private set

    fun connect(host: String, port: Int) {
        endpoint = "ws://$host:$port/"
        intentionalClose.set(false)
        open()
    }

    fun disconnect() {
        intentionalClose.set(true)
        reconnectJob?.cancel()
        socket?.close(1000, "bye")
        socket = null
        isConnected = false
        onConnectionChanged(false)
    }

    inline fun <reified T> send(type: String, payload: T? = null) {
        sendText(JoduJson.message(type, payload))
    }

    @PublishedApi
    internal fun sendText(json: String) {
        socket?.send(json)
    }

    private fun open() {
        val url = endpoint ?: return
        socket?.cancel()
        val request = Request.Builder().url(url).build()
        socket = client.newWebSocket(request, object : WebSocketListener() {
            override fun onOpen(webSocket: WebSocket, response: Response) {
                isConnected = true
                onConnectionChanged(true)
            }

            override fun onMessage(webSocket: WebSocket, text: String) {
                runCatching { JoduJson.parse(text) }.getOrNull()?.let(onMessage)
            }

            override fun onMessage(webSocket: WebSocket, bytes: ByteString) {
                onMessage(webSocket, bytes.utf8())
            }

            override fun onClosing(webSocket: WebSocket, code: Int, reason: String) {
                webSocket.close(code, reason)
            }

            override fun onClosed(webSocket: WebSocket, code: Int, reason: String) {
                isConnected = false
                onConnectionChanged(false)
                scheduleReconnect()
            }

            override fun onFailure(webSocket: WebSocket, t: Throwable, response: Response?) {
                isConnected = false
                onConnectionChanged(false)
                scheduleReconnect()
            }
        })
    }

    private fun scheduleReconnect() {
        if (intentionalClose.get()) return
        reconnectJob?.cancel()
        reconnectJob = scope.launch(Dispatchers.IO) {
            delay(2000)
            if (isActive && !intentionalClose.get()) open()
        }
    }
}
