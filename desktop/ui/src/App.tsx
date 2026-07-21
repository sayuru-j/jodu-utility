import { useEffect, useMemo, useState, type CSSProperties } from 'react'
import type { AppState } from './bridge'
import { sendCommand, subscribeState, uploadFiles } from './bridge'
import './App.css'

const initial: AppState = {
  connected: false,
  httpPort: 19285,
  wsPort: 19284,
}

export default function App() {
  const [state, setState] = useState<AppState>(initial)
  const [dragging, setDragging] = useState(false)
  const [note, setNote] = useState('')

  useEffect(() => subscribeState(setState), [])

  const peerBase = useMemo(() => {
    if (!state.peer?.ip) return ''
    return `http://${state.peer.ip}:${state.peer.httpPort}`
  }, [state.peer])

  const battery = state.telemetry?.batteryPercent ?? null
  const charging = state.telemetry?.isCharging ?? false
  const ssid = state.telemetry?.wifiSsid || '—'
  const device = state.telemetry?.deviceName || state.peer?.deviceName || 'phone'
  const playing = state.media?.isPlaying ?? false

  async function onDrop(files: FileList | null) {
    setDragging(false)
    if (!files?.length) return
    if (!peerBase) {
      setNote('no phone linked')
      return
    }
    try {
      setNote(`sending ${files.length}`)
      await uploadFiles(files, peerBase)
      setNote('sent')
    } catch {
      setNote('transfer failed')
    }
  }

  return (
    <div className="app">
      <header className="top">
        <div className="brand-block">
          <h1>JODU</h1>
          <p>pair</p>
        </div>
        <div className={`link ${state.connected ? 'on' : ''}`}>
          <span className="glyph" aria-hidden />
          <span>{state.connected ? device : 'waiting'}</span>
        </div>
      </header>

      <main className="grid">
        <section className="cell stats" aria-label="Phone status">
          <div className="stat">
            <span className="label">battery</span>
            <strong>
              {battery != null ? `${battery}%` : '—'}
              {charging ? <span className="chg"> chg</span> : null}
            </strong>
          </div>
          <div className="stat">
            <span className="label">wifi</span>
            <strong className="truncate">{ssid}</strong>
          </div>
          <div
            className="meter"
            style={{ '--level': `${battery ?? 0}%` } as CSSProperties}
            aria-hidden
          />
        </section>

        <section className="cell media" aria-label="Media">
          <div className="media-meta">
            <span className="label">now</span>
            <strong className="truncate">{state.media?.title || 'idle'}</strong>
            <span className="sub truncate">{state.media?.artist || 'no track'}</span>
          </div>
          <div className="transport">
            <button
              type="button"
              disabled={!state.connected}
              onClick={() => sendCommand({ action: 'MEDIA', value: 'PREVIOUS' })}
              aria-label="Previous"
            >
              ‹
            </button>
            <button
              type="button"
              className="play"
              disabled={!state.connected}
              onClick={() =>
                sendCommand({
                  action: 'MEDIA',
                  value: playing ? 'PAUSE' : 'PLAY',
                })
              }
              aria-label={playing ? 'Pause' : 'Play'}
            >
              {playing ? 'II' : '▶'}
            </button>
            <button
              type="button"
              disabled={!state.connected}
              onClick={() => sendCommand({ action: 'MEDIA', value: 'NEXT' })}
              aria-label="Next"
            >
              ›
            </button>
          </div>
        </section>

        <section
          className={`cell drop ${dragging ? 'active' : ''}`}
          onDragEnter={(e) => {
            e.preventDefault()
            setDragging(true)
          }}
          onDragOver={(e) => e.preventDefault()}
          onDragLeave={() => setDragging(false)}
          onDrop={(e) => {
            e.preventDefault()
            void onDrop(e.dataTransfer.files)
          }}
        >
          <span className="label">files</span>
          <strong>drop to phone</strong>
          <label className="pick">
            browse
            <input
              type="file"
              multiple
              onChange={(e) => void onDrop(e.target.files)}
            />
          </label>
          {note ? <span className="note">{note}</span> : null}
        </section>

        <section className="cell tools">
          <button
            type="button"
            className="solid"
            disabled={!state.connected}
            onClick={() => sendCommand({ action: 'PING' })}
          >
            ping
          </button>
          <button
            type="button"
            disabled={!state.clipboardPreview}
            onClick={() => sendCommand({ action: 'COPY_MOBILE_CLIPBOARD' })}
          >
            copy clip
          </button>
          <p className="clip truncate" title={state.clipboardPreview || ''}>
            {state.clipboardPreview || 'clipboard empty'}
          </p>
        </section>
      </main>

      <footer className="foot">
        <span>ws {state.wsPort}</span>
        <span className="dot-sep" />
        <span>http {state.httpPort}</span>
        {state.peer?.ip ? (
          <>
            <span className="dot-sep" />
            <span>{state.peer.ip}</span>
          </>
        ) : null}
      </footer>
    </div>
  )
}
