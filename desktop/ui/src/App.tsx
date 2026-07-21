import { useEffect, useMemo, useState } from 'react'
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
  const [transferNote, setTransferNote] = useState('')

  useEffect(() => subscribeState(setState), [])

  const peerBase = useMemo(() => {
    if (!state.peer?.ip) return ''
    return `http://${state.peer.ip}:${state.peer.httpPort}`
  }, [state.peer])

  const battery = state.telemetry?.batteryPercent ?? null
  const charging = state.telemetry?.isCharging ?? false
  const ssid = state.telemetry?.wifiSsid || '—'
  const device = state.telemetry?.deviceName || state.peer?.deviceName || 'Phone'

  async function onDrop(files: FileList | null) {
    setDragging(false)
    if (!files?.length) return
    if (!peerBase) {
      setTransferNote('Connect a phone before sending files.')
      return
    }
    try {
      setTransferNote(`Sending ${files.length} file(s)…`)
      await uploadFiles(files, peerBase)
      setTransferNote('Transfer complete.')
    } catch {
      setTransferNote('Transfer failed. Check LAN connection.')
    }
  }

  return (
    <div className="shell">
      <header className="brand-bar">
        <div className="brand">
          <span className="brand-mark" aria-hidden />
          <h1>JODU</h1>
        </div>
        <p className="tagline">Pair your phone and PC on the local network.</p>
        <div className={`link-pill ${state.connected ? 'on' : 'off'}`}>
          <span className="dot" />
          {state.connected ? `Linked · ${device}` : 'Waiting for phone'}
        </div>
      </header>

      <section className="status" aria-label="Phone status">
        <div className="status-main">
          <p className="eyebrow">Phone status</p>
          <h2>{device}</h2>
          <p className="meta">
            {battery != null ? `${battery}%${charging ? ' charging' : ''}` : 'Battery —'}
            <span className="sep" />
            Wi-Fi {ssid}
          </p>
        </div>
        <div className="battery-ring" data-charging={charging ? '1' : '0'}>
          <strong>{battery != null ? `${battery}%` : '—'}</strong>
        </div>
      </section>

      <section className="media" aria-label="Media controls">
        <div className="media-copy">
          <p className="eyebrow">Now playing</p>
          <h3>{state.media?.title || 'Nothing playing'}</h3>
          <p>{state.media?.artist || 'Controls sync when media is active on your phone.'}</p>
        </div>
        <div className="media-actions">
          <button
            type="button"
            disabled={!state.connected}
            onClick={() => sendCommand({ action: 'MEDIA', value: 'PREVIOUS' })}
            aria-label="Previous"
          >
            ‹‹
          </button>
          <button
            type="button"
            className="primary"
            disabled={!state.connected}
            onClick={() =>
              sendCommand({
                action: 'MEDIA',
                value: state.media?.isPlaying ? 'PAUSE' : 'PLAY',
              })
            }
          >
            {state.media?.isPlaying ? 'Pause' : 'Play'}
          </button>
          <button
            type="button"
            disabled={!state.connected}
            onClick={() => sendCommand({ action: 'MEDIA', value: 'NEXT' })}
            aria-label="Next"
          >
            ››
          </button>
        </div>
      </section>

      <section className="actions">
        <div
          className={`dropzone ${dragging ? 'active' : ''}`}
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
          <p className="eyebrow">File transfer</p>
          <h3>Drop files to send</h3>
          <p>Streams over local HTTP to the phone Downloads folder.</p>
          <label className="file-pick">
            Choose files
            <input
              type="file"
              multiple
              onChange={(e) => void onDrop(e.target.files)}
            />
          </label>
          {transferNote ? <p className="note">{transferNote}</p> : null}
        </div>

        <div className="side-actions">
          <button
            type="button"
            className="ping"
            disabled={!state.connected}
            onClick={() => sendCommand({ action: 'PING' })}
          >
            Ping phone
          </button>
          <button
            type="button"
            disabled={!state.clipboardPreview}
            onClick={() => sendCommand({ action: 'COPY_MOBILE_CLIPBOARD' })}
          >
            Copy mobile clipboard
          </button>
          <p className="clip">
            <span className="eyebrow">Latest phone clip</span>
            {state.clipboardPreview || 'Nothing synced yet'}
          </p>
          <p className="ports">
            WS {state.wsPort} · HTTP {state.httpPort}
            {state.peer?.ip ? ` · peer ${state.peer.ip}` : ''}
          </p>
        </div>
      </section>
    </div>
  )
}
