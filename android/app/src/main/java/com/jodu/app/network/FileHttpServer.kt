package com.jodu.app.network

import android.content.ContentValues
import android.content.Context
import android.os.Build
import android.os.Environment
import android.provider.MediaStore
import android.webkit.MimeTypeMap
import com.jodu.app.protocol.JoduPorts
import fi.iki.elonen.NanoHTTPD
import java.io.File
import java.io.FileOutputStream
import java.io.OutputStream

class FileHttpServer(private val context: Context) : NanoHTTPD(JoduPorts.FILE_HTTP) {
    var onFileReceived: ((String) -> Unit)? = null
    var onProgress: ((fileName: String, transferred: Long, total: Long) -> Unit)? = null

    override fun serve(session: IHTTPSession): Response {
        if (session.method == Method.OPTIONS) {
            return newFixedLengthResponse(Response.Status.NO_CONTENT, MIME_PLAINTEXT, "")
                .also(::addCors)
        }

        if (session.method != Method.POST || session.uri != "/upload") {
            return newFixedLengthResponse(Response.Status.NOT_FOUND, MIME_PLAINTEXT, "not found")
        }

        return try {
            val fileName = session.headers["x-filename"]
                ?.substringAfterLast('/')
                ?.substringAfterLast('\\')
                ?.takeIf { it.isNotBlank() }
                ?: "jodu-${System.currentTimeMillis()}.bin"

            val total = session.headers["content-length"]?.toLongOrNull() ?: -1L
            onProgress?.invoke(fileName, 0L, total.coerceAtLeast(0L))

            val saved = saveIncoming(fileName) { out ->
                copyWithProgress(session.inputStream, out, fileName, total)
            }

            onProgress?.invoke(fileName, total.coerceAtLeast(0L), total.coerceAtLeast(0L))
            onFileReceived?.invoke(saved)
            newFixedLengthResponse(
                Response.Status.OK,
                "application/json",
                """{"ok":true,"path":"$saved"}""",
            ).also(::addCors)
        } catch (e: Exception) {
            newFixedLengthResponse(
                Response.Status.INTERNAL_ERROR,
                MIME_PLAINTEXT,
                e.message ?: "error",
            )
        }
    }

    private fun copyWithProgress(
        input: java.io.InputStream,
        output: OutputStream,
        fileName: String,
        total: Long,
    ) {
        val buffer = ByteArray(64 * 1024)
        var transferred = 0L
        var lastReport = 0L
        while (true) {
            val read = input.read(buffer)
            if (read <= 0) break
            output.write(buffer, 0, read)
            transferred += read
            if (transferred - lastReport >= 256 * 1024 || (total > 0 && transferred >= total)) {
                lastReport = transferred
                onProgress?.invoke(fileName, transferred, total.coerceAtLeast(0L))
            }
        }
        onProgress?.invoke(fileName, transferred, if (total > 0) total else transferred)
    }

    private fun saveIncoming(fileName: String, write: (OutputStream) -> Unit): String {
        val mime = guessMime(fileName)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, fileName)
                put(MediaStore.Downloads.MIME_TYPE, mime)
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val resolver = context.contentResolver
            val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                ?: error("unable to create download entry — grant storage access")
            try {
                resolver.openOutputStream(uri)?.use(write) ?: error("unable to open download stream")
                values.clear()
                values.put(MediaStore.Downloads.IS_PENDING, 0)
                resolver.update(uri, values, null, null)
                return uri.toString()
            } catch (e: Exception) {
                runCatching { resolver.delete(uri, null, null) }
                throw e
            }
        }

        val downloads = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
        if (!downloads.exists() && !downloads.mkdirs()) {
            error("unable to access Downloads — grant storage permission")
        }
        val dest = uniqueFile(File(downloads, fileName))
        FileOutputStream(dest).use(write)
        return dest.absolutePath
    }

    private fun guessMime(fileName: String): String {
        val ext = fileName.substringAfterLast('.', "").lowercase()
        return MimeTypeMap.getSingleton().getMimeTypeFromExtension(ext)
            ?: "application/octet-stream"
    }

    private fun addCors(response: Response) {
        response.addHeader("Access-Control-Allow-Origin", "*")
        response.addHeader("Access-Control-Allow-Methods", "POST, OPTIONS")
        response.addHeader("Access-Control-Allow-Headers", "Content-Type, X-Filename")
    }

    private fun uniqueFile(file: File): File {
        if (!file.exists()) return file
        val name = file.nameWithoutExtension
        val ext = file.extension.let { if (it.isEmpty()) "" else ".$it" }
        var i = 1
        var candidate: File
        do {
            candidate = File(file.parentFile, "$name ($i)$ext")
            i++
        } while (candidate.exists())
        return candidate
    }
}
