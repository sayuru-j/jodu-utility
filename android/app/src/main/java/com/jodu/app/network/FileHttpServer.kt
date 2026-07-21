package com.jodu.app.network

import android.os.Environment
import com.jodu.app.protocol.JoduPorts
import fi.iki.elonen.NanoHTTPD
import java.io.File
import java.io.FileInputStream
import java.io.FileOutputStream

class FileHttpServer : NanoHTTPD(JoduPorts.FILE_HTTP) {
    var onFileReceived: ((File) -> Unit)? = null

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

            val downloads = Environment.getExternalStoragePublicDirectory(Environment.DIRECTORY_DOWNLOADS)
            downloads.mkdirs()
            val dest = uniqueFile(File(downloads, fileName))

            val tmpPath = bodyFiles["postData"]
            if (tmpPath != null) {
                FileInputStream(tmpPath).use { input ->
                    FileOutputStream(dest).use { output -> input.copyTo(output) }
                }
            } else {
                session.inputStream.use { input ->
                    FileOutputStream(dest).use { output -> input.copyTo(output) }
                }
            }

            onFileReceived?.invoke(dest)
            newFixedLengthResponse(
                Response.Status.OK,
                "application/json",
                """{"ok":true,"path":"${dest.absolutePath}"}""",
            ).also(::addCors)
        } catch (e: Exception) {
            newFixedLengthResponse(
                Response.Status.INTERNAL_ERROR,
                MIME_PLAINTEXT,
                e.message ?: "error",
            )
        }
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
