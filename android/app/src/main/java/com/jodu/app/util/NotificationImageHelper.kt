package com.jodu.app.util

import android.app.Notification
import android.content.Context
import android.graphics.Bitmap
import android.graphics.Canvas
import android.graphics.drawable.BitmapDrawable
import android.graphics.drawable.Icon
import android.os.Build
import android.os.Bundle
import android.util.Base64
import java.io.ByteArrayOutputStream
import kotlin.math.max

object NotificationImageHelper {
    private const val MaxEdge = 240
    private const val JpegQuality = 72
    private const val MaxBase64Chars = 60_000

    fun extractThumbnailBase64(context: Context, notification: Notification): String? {
        val bitmap = extractBitmap(context, notification) ?: return null
        return encodeThumbnail(bitmap)
    }

    private fun extractBitmap(context: Context, notification: Notification): Bitmap? {
        val extras = notification.extras
        bigPicture(extras, context)?.let { return it }
        notification.getLargeIcon()?.toBitmap(context)?.let { return it }
        largeIconFromExtras(extras, context)?.let { return it }
        return null
    }

    private fun bigPicture(extras: Bundle, context: Context): Bitmap? {
        val picture = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
            extras.getParcelable(Notification.EXTRA_PICTURE, Bitmap::class.java)
        } else {
            @Suppress("DEPRECATION")
            extras.get(Notification.EXTRA_PICTURE) as? Bitmap
        }
        if (picture != null) return picture

        if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.S) {
            val icon = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                extras.getParcelable(Notification.EXTRA_PICTURE_ICON, Icon::class.java)
            } else {
                @Suppress("DEPRECATION")
                extras.getParcelable(Notification.EXTRA_PICTURE_ICON) as? Icon
            }
            icon?.toBitmap(context)?.let { return it }
        }
        return null
    }

    private fun largeIconFromExtras(extras: Bundle, context: Context): Bitmap? {
        val candidates = listOf(
            Notification.EXTRA_LARGE_ICON_BIG,
            Notification.EXTRA_LARGE_ICON,
        )
        for (key in candidates) {
            val raw = if (Build.VERSION.SDK_INT >= Build.VERSION_CODES.TIRAMISU) {
                extras.getParcelable(key, Bitmap::class.java)
                    ?: extras.getParcelable(key, Icon::class.java)
            } else {
                @Suppress("DEPRECATION")
                extras.get(key)
            }
            when (raw) {
                is Bitmap -> return raw
                is Icon -> raw.toBitmap(context)?.let { return it }
            }
        }
        return null
    }

    private fun Icon.toBitmap(context: Context): Bitmap? {
        return try {
            val drawable = loadDrawable(context) ?: return null
            when (drawable) {
                is BitmapDrawable -> drawable.bitmap?.takeIf { !it.isRecycled }
                else -> {
                    val w = max(drawable.intrinsicWidth, 1).coerceAtMost(MaxEdge)
                    val h = max(drawable.intrinsicHeight, 1).coerceAtMost(MaxEdge)
                    val bmp = Bitmap.createBitmap(w, h, Bitmap.Config.ARGB_8888)
                    val canvas = Canvas(bmp)
                    drawable.setBounds(0, 0, w, h)
                    drawable.draw(canvas)
                    bmp
                }
            }
        } catch (_: Exception) {
            null
        }
    }

    private fun encodeThumbnail(source: Bitmap): String? {
        return try {
            val scaled = scaleDown(source, MaxEdge)
            val stream = ByteArrayOutputStream()
            var quality = JpegQuality
            if (!scaled.compress(Bitmap.CompressFormat.JPEG, quality, stream)) {
                if (scaled !== source) scaled.recycle()
                return null
            }
            var bytes = stream.toByteArray()
            while (bytes.size > MaxBase64Chars * 3 / 4 && quality > 40) {
                quality -= 10
                stream.reset()
                scaled.compress(Bitmap.CompressFormat.JPEG, quality, stream)
                bytes = stream.toByteArray()
            }
            if (scaled !== source) scaled.recycle()
            val b64 = Base64.encodeToString(bytes, Base64.NO_WRAP)
            if (b64.length > MaxBase64Chars) null else b64
        } catch (_: Exception) {
            null
        }
    }

    private fun scaleDown(source: Bitmap, maxEdge: Int): Bitmap {
        val w = source.width
        val h = source.height
        val edge = max(w, h)
        if (edge <= maxEdge) return source
        val scale = maxEdge.toFloat() / edge
        val nw = max(1, (w * scale).toInt())
        val nh = max(1, (h * scale).toInt())
        return Bitmap.createScaledBitmap(source, nw, nh, true)
    }
}
