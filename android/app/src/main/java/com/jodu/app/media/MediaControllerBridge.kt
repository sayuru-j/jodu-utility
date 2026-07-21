package com.jodu.app.media

import android.content.Context
import android.media.AudioManager
import android.media.session.MediaController
import android.media.session.MediaSessionManager
import android.media.session.PlaybackState
import com.jodu.app.protocol.MediaActions
import com.jodu.app.protocol.MediaStatePayload

class MediaControllerBridge(private val context: Context) {
    private val sessionManager =
        context.getSystemService(Context.MEDIA_SESSION_SERVICE) as MediaSessionManager
    private val audioManager =
        context.getSystemService(Context.AUDIO_SERVICE) as AudioManager

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
        val controller = activeController()
        val controls = controller?.transportControls
        when (action.uppercase()) {
            MediaActions.PLAY -> controls?.play()
            MediaActions.PAUSE -> controls?.pause()
            MediaActions.NEXT -> controls?.skipToNext()
            MediaActions.PREVIOUS -> controls?.skipToPrevious()
            MediaActions.VOLUME_UP -> audioManager.adjustStreamVolume(
                AudioManager.STREAM_MUSIC,
                AudioManager.ADJUST_RAISE,
                0,
            )
            MediaActions.VOLUME_DOWN -> audioManager.adjustStreamVolume(
                AudioManager.STREAM_MUSIC,
                AudioManager.ADJUST_LOWER,
                0,
            )
        }
    }

    private fun activeController(): MediaController? {
        return try {
            sessionManager.getActiveSessions(null)
                .firstOrNull { it.playbackState != null }
        } catch (_: SecurityException) {
            null
        }
    }
}
