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
import okio.source
import java.util.concurrent.TimeUnit

object FileUploadClient {
    private val client = OkHttpClient.Builder()
        .connectTimeout(15, TimeUnit.SECONDS)
        .writeTimeout(5, TimeUnit.MINUTES)
        .readTimeout(5, TimeUnit.MINUTES)
        .build()

    suspend fun upload(
        context: Context,
        peer: DiscoveryPayload,
        uri: Uri,
    ): Result<Unit> = withContext(Dispatchers.IO) {
        runCatching {
            val name = resolveDisplayName(context, uri)
            val mime = context.contentResolver.getType(uri) ?: "application/octet-stream"
            val body = object : RequestBody() {
                override fun contentType() = mime.toMediaType()

                override fun writeTo(sink: BufferedSink) {
                    context.contentResolver.openInputStream(uri)?.use { input ->
                        sink.writeAll(input.source())
                    } ?: error("unable to open $uri")
                }
            }

            val request = Request.Builder()
                .url("http://${peer.ip}:${peer.httpPort}/upload")
                .header("X-Filename", name)
                .post(body)
                .build()

            client.newCall(request).execute().use { response ->
                if (!response.isSuccessful) {
                    error("upload failed: HTTP ${response.code}")
                }
            }
        }
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
}
