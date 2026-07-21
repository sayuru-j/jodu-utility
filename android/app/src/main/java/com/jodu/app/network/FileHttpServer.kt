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

class FileHttpServer(private val context: Context) : NanoHTTPD(JoduPorts.FILE_HTTP) {
    var onFileReceived: ((String) -> Unit)? = null

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

            val bodyFiles = HashMap<String, String>()
            session.parseBody(bodyFiles)

            val tmpPath = bodyFiles["postData"]
            val saved = if (tmpPath != null) {
                saveIncoming(fileName) { out ->
                    File(tmpPath).inputStream().use { it.copyTo(out) }
                }
            } else {
                saveIncoming(fileName) { out ->
                    session.inputStream.use { it.copyTo(out) }
                }
            }

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

    private fun saveIncoming(fileName: String, write: (java.io.OutputStream) -> Unit): String {
        val mime = guessMime(fileName)
        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.Q) {
            val values = ContentValues().apply {
                put(MediaStore.Downloads.DISPLAY_NAME, fileName)
                put(MediaStore.Downloads.MIME_TYPE, mime)
                put(MediaStore.Downloads.IS_PENDING, 1)
            }
            val resolver = context.contentResolver
            val uri = resolver.insert(MediaStore.Downloads.EXTERNAL_CONTENT_URI, values)
                ?: error("unable to create download entry")
            resolver.openOutputStream(uri)?.use(write) ?: error("unable to open download stream")
            values.clear()
            values.put(MediaStore.Downloads.IS_PENDING, 0)
            resolver.update(uri, values, null, null)
            return uri.toString()
        }

        val downloads = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
        downloads.mkdirs()
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
