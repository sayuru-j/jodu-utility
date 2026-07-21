package com.jodu.app.media

import android.content.ComponentName
import android.content.Context
import android.media.AudioManager
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import com.jodu.app.protocol.MediaActions
import com.jodu.app.protocol.MediaStatePayload
import com.jodu.app.service.OtpNotificationListener

class MediaControllerBridge(private val context: Context) {
    private val sessionManager =
        context.getSystemService(Context.MEDIA_SESSION_SERVICE) as MediaSessionManager
    private val audioManager =
        context.getSystemService(Context.AUDIO_SERVICE) as AudioManager
    private val listenerComponent =
        ComponentName(context, OtpNotificationListener::class.java)

    fun activeState(): MediaStatePayload {
        val controller = activeController()
        val metadata = controller?.metadata
        val playing = controller?.playbackState?.state == PlaybackState.STATE_PLAYING
        val volume = audioManager.getStreamVolume(AudioManager.STREAM_MUSIC)
        val max = audioManager.getStreamMaxVolume(AudioManager.STREAM_MUSIC).coerceAtLeast(1)
        return MediaStatePayload(
            title = metadata?.getString(android.media.MediaMetadata.METADATA_KEY_TITLE),
            artist = metadata?.getString(android.media.MediaMetadata.METADATA_KEY_ARTIST),
            isPlaying = playing,
            volume = ((volume * 100f) / max).toInt(),
        )
    }

    fun handle(action: String) {
        val controller = activeController() ?: return
        val controls = controller.transportControls ?: return
        val state = controller.playbackState?.state
        when (action.uppercase()) {
            MediaActions.PLAY -> {
                if (state != PlaybackState.STATE_PLAYING) controls.play()
            }
            MediaActions.PAUSE -> {
                if (state == PlaybackState.STATE_PLAYING || state == PlaybackState.STATE_BUFFERING) {
                    controls.pause()
                }
            }
            MediaActions.NEXT -> controls.skipToNext()
            MediaActions.PREVIOUS -> controls.skipToPrevious()
            MediaActions.VOLUME_UP -> audioManager.adjustStreamVolume(
                AudioManager.STREAM_MUSIC,
                AudioManager.ADJUST_RAISE,
                AudioManager.FLAG_SHOW_UI,
            )
            MediaActions.VOLUME_DOWN -> audioManager.adjustStreamVolume(
                AudioManager.STREAM_MUSIC,
                AudioManager.ADJUST_LOWER,
                AudioManager.FLAG_SHOW_UI,
            )
        }
    }

    private fun activeController(): MediaController? {
        val sessions = try {
            sessionManager.getActiveSessions(listenerComponent)
        } catch (_: SecurityException) {
            // Notification listener not enabled — media control unavailable.
            return null
        } catch (_: Exception) {
            return null
        }

        if (sessions.isEmpty()) return null

        return sessions.firstOrNull {
            it.playbackState?.state == PlaybackState.STATE_PLAYING
        } ?: sessions.firstOrNull {
            it.playbackState?.state == PlaybackState.STATE_BUFFERING
        } ?: sessions.firstOrNull {
            it.metadata != null
        } ?: sessions.firstOrNull {
            it.playbackState != null
        } ?: sessions.firstOrNull()
    }
}
