package com.jodu.app.network

import android.content.Context
import android.net.Uri
import android.provider.OpenableColumns
import com.jodu.app.protocol.DiscoveryPayload
import kotlinx.coroutines.Dispatchers
import kotlinx.coroutines.withContext
import okhttp3.MediaType.Companion.toMediaType
import okhttp3.OkHttpClient
import okhttp3.Request
import okhttp3.RequestBody
import okio.BufferedSink
import java.util.concurrent.TimeUnit

object FileUploadClient {
    /** Desktop file HTTP port (android uses 19286 for its own receiver). */
    private const val DESKTOP_FILE_HTTP = 19285

    private val client = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .writeTimeout(5, TimeUnit.MINUTES)
        .readTimeout(5, TimeUnit.MINUTES)
        .build()

    suspend fun upload(
        context: Context,
        peer: DiscoveryPayload,
        uri: Uri,
        onProgress: ((fileName: String, transferred: Long, total: Long) -> Unit)? = null,
    ): Result<Unit> = withContext(Dispatchers.IO) {
        runCatching {
            val name = resolveDisplayName(context, uri)
            val mime = context.contentResolver.getType(uri) ?: "application/octet-stream"
            val total = resolveSize(context, uri)
            val body = object : RequestBody() {
                override fun contentType() = mime.toMediaType()

                override fun contentLength(): Long = if (total > 0) total else -1L

                override fun writeTo(sink: BufferedSink) {
                    context.contentResolver.openInputStream(uri)?.use { input ->
                        val buffer = ByteArray(64 * 1024)
                        var transferred = 0L
                        var lastReport = 0L
                        while (true) {
                            val read = input.read(buffer)
                            if (read <= 0) break
                            sink.write(buffer, 0, read)
                            transferred += read
                            if (transferred - lastReport >= 256 * 1024 || (total > 0 && transferred >= total)) {
                                lastReport = transferred
                                onProgress?.invoke(name, transferred, total.coerceAtLeast(0L))
                            }
                        }
                        onProgress?.invoke(name, transferred, if (total > 0) total else transferred)
                    } ?: error("unable to open $uri")
                }
            }

            val port = resolveDesktopUploadPort(peer)
            val request = Request.Builder()
                .url("http://${peer.ip}:$port/upload")
                .header("X-Filename", name)
                .post(body)
                .build()

            onProgress?.invoke(name, 0L, total.coerceAtLeast(0L))
            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    error("upload failed: HTTP ${response.code}")
                }
            }
        }
    }

    private fun resolveDesktopUploadPort(peer: DiscoveryPayload): Int {
        val port = peer.httpPort
        // Phone's own receiver port must never be used when talking to a desktop.
        if (peer.role.equals("desktop", ignoreCase = true)) {
            if (port <= 0 || port == com.jodu.app.protocol.JoduPorts.FILE_HTTP) return DESKTOP_FILE_HTTP
            return port
        }
        return port.takeIf { it > 0 } ?: DESKTOP_FILE_HTTP
    }

    private fun resolveDisplayName(context: Context, uri: Uri): String {
        context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val index = cursor.getColumnIndex(OpenableColumns.DISPLAY_NAME)
            if (index >= 0 && cursor.moveToFirst()) {
                val name = cursor.getString(index)
                if (!name.isNullOrBlank()) return name
            }
        }
        return uri.lastPathSegment?.substringAfterLast('/')
            ?: "jodu-${System.currentTimeMillis()}.bin"
    }

    private fun resolveSize(context: Context, uri: Uri): Long {
        context.contentResolver.query(uri, null, null, null, null)?.use { cursor ->
            val index = cursor.getColumnIndex(OpenableColumns.SIZE)
            if (index >= 0 && cursor.moveToFirst()) {
                val size = cursor.getLong(index)
                if (size > 0) return size
            }
        }
        return -1L
    }
}
