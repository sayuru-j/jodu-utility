import { useEffect, useMemo, useState, type CSSProperties } from 'react'
import { AnimatePresence, motion } from 'framer-motion'
import type { AppState, Peer, Telemetry } from './bridge'
import { resolvePeerUploadBase, sendCommand, subscribeState, uploadFiles } from './bridge'
import './App.css'

const initial: AppState = {
  connected: false,
  httpPort: 19285,
  wsPort: 19284,
  maximized: false,
  peers: [],
  pairStatus: 'idle',
}

const fadeUp = {
  initial: { opacity: 0, y: 10 },
  animate: { opacity: 1, y: 0 },
  exit: { opacity: 0, y: -6 },
}

const stagger = {
  animate: { transition: { staggerChildren: 0.06, delayChildren: 0.04 } },
}

function wifiLabel(telemetry?: Telemetry | null): string {
  if (!telemetry) return '—'
  if (telemetry.wifiSsid) return telemetry.wifiSsid
  if (telemetry.wifiConnected) {
    if (telemetry.wifiRssi != null) return `Wi‑Fi · ${telemetry.wifiRssi} dBm`
    return 'Wi‑Fi'
  }
  return 'offline'
}

export default function App() {
  const [state, setState] = useState<AppState>(initial)
  const [dragging, setDragging] = useState(false)
  const [note, setNote] = useState('')
  const [settingsOpen, setSettingsOpen] = useState(false)
  const [previewingNotification, setPreviewingNotification] = useState(false)
  const [previewingCall, setPreviewingCall] = useState(false)

  useEffect(() => subscribeState(setState), [])

  useEffect(() => {
    const onToneEnded = (e: Event) => {
      const slot = (e as CustomEvent<string>).detail
      if (slot === '__joduTone') setPreviewingNotification(false)
      if (slot === '__joduCallTone') setPreviewingCall(false)
    }
    window.addEventListener('jodu-tone-ended', onToneEnded)
    return () => window.removeEventListener('jodu-tone-ended', onToneEnded)
  }, [])

  useEffect(() => {
    if (!settingsOpen) {
      sendCommand({ action: 'STOP_NOTIFICATION_TONE' })
      sendCommand({ action: 'STOP_INCOMING_CALL_TONE' })
      setPreviewingNotification(false)
      setPreviewingCall(false)
      return
    }
    const onKey = (e: KeyboardEvent) => {
      if (e.key === 'Escape') setSettingsOpen(false)
    }
    window.addEventListener('keydown', onKey)
    return () => window.removeEventListener('keydown', onKey)
  }, [settingsOpen])

  const peerBase = useMemo(
    () => resolvePeerUploadBase(state.peer, state.peers, state.connected),
    [state.peer, state.peers, state.connected],
  )

  const battery = state.telemetry?.batteryPercent ?? null
  const charging = state.telemetry?.isCharging ?? false
  const ssid = wifiLabel(state.telemetry)
  const device = state.telemetry?.deviceName || state.peer?.deviceName || 'phone'
  const playing = state.media?.isPlaying ?? false
  const peers = useMemo(() => {
    const byIp = new Map<string, Peer>()
    for (const p of state.peers ?? []) {
      byIp.set((p.ip || p.deviceId).toLowerCase(), p)
    }
    if (state.connected && state.peer) {
      byIp.set((state.peer.ip || state.peer.deviceId).toLowerCase(), state.peer)
    }
    return [...byIp.values()]
  }, [state.peers, state.peer, state.connected])

  const transferNote = useMemo(() => {
    const t = state.transfer
    if (note) return note
    if (!t?.fileName) return ''
    if (t.status === 'done') {
      return t.direction === 'send' ? `sent · ${t.fileName}` : `received · ${t.fileName}`
    }
    if (t.status === 'error') return `failed · ${t.error || t.fileName}`
    const pct = t.percent >= 0 ? ` ${t.percent}%` : ''
    return t.direction === 'send'
      ? `sending · ${t.fileName}${pct}`
      : `receiving · ${t.fileName}${pct}`
  }, [state.transfer, note])

  const barPercent = useMemo(() => {
    const fromNote = note.match(/(\d+)%/)
    if (fromNote) return Number(fromNote[1])
    if (state.transfer?.status === 'progress' && state.transfer.percent >= 0) {
      return state.transfer.percent
    }
    return null
  }, [note, state.transfer])

  async function onDrop(files: FileList | null) {
    setDragging(false)
    if (!files?.length) return
    if (!state.connected && !peerBase) {
      setNote('no phone linked')
      return
    }
    if (!peerBase) {
      setNote('no phone linked')
      return
    }
    try {
      const total = files.length
      await uploadFiles(files, peerBase, (fileName, percent, index) => {
        setNote(`sending · ${fileName} ${percent}% (${index + 1}/${total})`)
      })
      setNote(total === 1 ? 'sent' : `sent ${total} files`)
    } catch {
      setNote('transfer failed')
    }
  }

  return (
    <motion.div
      className={`app ${settingsOpen ? 'settings-open' : ''}`}
      initial={{ opacity: 0 }}
      animate={{ opacity: 1 }}
      transition={{ duration: 0.35 }}
    >
      <div
        className="titlebar"
        onMouseDown={(e) => {
          if ((e.target as HTMLElement).closest('button')) return
          if (e.button !== 0) return
          sendCommand({ action: 'WINDOW_DRAG' })
        }}
        onDoubleClick={() => sendCommand({ action: 'WINDOW_MAXIMIZE' })}
      >
        <div className="titlebar-brand">
          <span className="title-name">JODU</span>
        </div>
        <div className={`title-link ${state.connected ? 'on' : ''}`}>
          <span>{state.connected ? `paired · ${device}` : 'waiting'}</span>
        </div>
        <div className="window-controls">
          <button
            type="button"
            className={settingsOpen ? 'active' : ''}
            aria-label="Settings"
            aria-pressed={settingsOpen}
            onClick={() => setSettingsOpen((v) => !v)}
          >
            <span className="ico ico-settings" />
          </button>
          <button
            type="button"
            aria-label="Minimize"
            onClick={() => sendCommand({ action: 'WINDOW_MINIMIZE' })}
          >
            <span className="ico ico-min" />
          </button>
          <button
            type="button"
            aria-label={state.maximized ? 'Restore' : 'Maximize'}
            onClick={() => sendCommand({ action: 'WINDOW_MAXIMIZE' })}
          >
            <span className={`ico ${state.maximized ? 'ico-restore' : 'ico-max'}`} />
          </button>
          <button
            type="button"
            className="close"
            aria-label="Close"
            onClick={() => sendCommand({ action: 'WINDOW_CLOSE' })}
          >
            <span className="ico ico-close" />
          </button>
        </div>
      </div>

      <AnimatePresence mode="popLayout">
        {state.incomingPair ? (
          <motion.section
            key="pair"
            className="pair-banner"
            aria-label="Incoming pair request"
            {...fadeUp}
            transition={{ type: 'spring', stiffness: 380, damping: 28 }}
            layout
          >
            <div>
              <span className="label">pair request</span>
              <strong>{state.incomingPair.deviceName}</strong>
              <span className="sub">{state.incomingPair.ip}</span>
            </div>
            <div className="pair-actions">
              <button type="button" onClick={() => sendCommand({ action: 'PAIR_REJECT' })}>
                decline
              </button>
              <button
                type="button"
                className="solid"
                onClick={() => sendCommand({ action: 'PAIR_ACCEPT' })}
              >
                accept
              </button>
            </div>
          </motion.section>
        ) : null}
      </AnimatePresence>

      <AnimatePresence mode="wait">
        {settingsOpen ? (
          <motion.section
            key="settings"
            className="settings"
            aria-label="Settings"
            {...fadeUp}
            transition={{ duration: 0.28 }}
          >
            <div className="settings-head">
              <h2>settings</h2>
              <button type="button" className="text-btn" onClick={() => setSettingsOpen(false)}>
                close
              </button>
            </div>
            <div className="settings-grid">
              <div className="settings-row settings-row-toggle">
                <div className="settings-copy">
                  <span className="label">startup</span>
                  <strong>Start with Windows</strong>
                  <span className="hint">launch minimized to tray</span>
                </div>
                <button
                  type="button"
                  role="switch"
                  aria-checked={!!state.startWithWindows}
                  className={`settings-switch ${state.startWithWindows ? 'on' : ''}`}
                  onClick={() =>
                    sendCommand({
                      action: 'SET_START_WITH_WINDOWS',
                      value: state.startWithWindows ? '0' : '1',
                    })
                  }
                />
              </div>
              <div className="settings-row settings-row-toggle">
                <div className="settings-copy">
                  <span className="label">auto connect</span>
                  <strong>Last paired phone</strong>
                  <span className="hint">
                    {state.lastPeerDeviceName
                      ? `reconnect to ${state.lastPeerDeviceName} when on LAN`
                      : 'pair once to remember a device'}
                  </span>
                </div>
                <button
                  type="button"
                  role="switch"
                  aria-checked={!!state.autoConnectLastDevice}
                  className={`settings-switch ${state.autoConnectLastDevice ? 'on' : ''}`}
                  onClick={() =>
                    sendCommand({
                      action: 'SET_AUTO_CONNECT',
                      value: state.autoConnectLastDevice ? '0' : '1',
                    })
                  }
                />
              </div>
              <div className="settings-row settings-row-tone">
                <div className="settings-copy">
                  <span className="label">notification tone</span>
                  <strong className="truncate">
                    {state.notificationToneIsCustom
                      ? state.notificationToneName || 'custom'
                      : 'default'}
                  </strong>
                  <span className="hint">
                    {state.notificationToneIsCustom
                      ? 'custom file · plays on phone alerts'
                      : 'bundled tone · plays on phone alerts'}
                  </span>
                </div>
                <div className="tone-actions">
                  <button
                    type="button"
                    className={previewingNotification ? 'tone-stop' : undefined}
                    onClick={() => {
                      if (previewingNotification) {
                        sendCommand({ action: 'STOP_NOTIFICATION_TONE' })
                        setPreviewingNotification(false)
                        return
                      }
                      sendCommand({ action: 'PREVIEW_NOTIFICATION_TONE' })
                      setPreviewingNotification(true)
                    }}
                  >
                    {previewingNotification ? 'stop' : 'preview'}
                  </button>
                  <button
                    type="button"
                    onClick={() => sendCommand({ action: 'PICK_NOTIFICATION_TONE' })}
                  >
                    choose
                  </button>
                  <button
                    type="button"
                    disabled={!state.notificationToneIsCustom}
                    onClick={() => sendCommand({ action: 'RESET_NOTIFICATION_TONE' })}
                  >
                    reset
                  </button>
                </div>
              </div>
              <div className="settings-row settings-row-tone">
                <div className="settings-copy">
                  <span className="label">incoming call tone</span>
                  <strong className="truncate">
                    {state.incomingCallToneIsCustom
                      ? state.incomingCallToneName || 'custom'
                      : 'default'}
                  </strong>
                  <span className="hint">
                    {state.incomingCallToneIsCustom
                      ? 'custom file · plays on ringing calls'
                      : 'bundled tone · plays on ringing calls'}
                  </span>
                </div>
                <div className="tone-actions">
                  <button
                    type="button"
                    className={previewingCall ? 'tone-stop' : undefined}
                    onClick={() => {
                      if (previewingCall) {
                        sendCommand({ action: 'STOP_INCOMING_CALL_TONE' })
                        setPreviewingCall(false)
                        return
                      }
                      sendCommand({ action: 'PREVIEW_INCOMING_CALL_TONE' })
                      setPreviewingCall(true)
                    }}
                  >
                    {previewingCall ? 'stop' : 'preview'}
                  </button>
                  <button
                    type="button"
                    onClick={() => sendCommand({ action: 'PICK_INCOMING_CALL_TONE' })}
                  >
                    choose
                  </button>
                  <button
                    type="button"
                    disabled={!state.incomingCallToneIsCustom}
                    onClick={() => sendCommand({ action: 'RESET_INCOMING_CALL_TONE' })}
                  >
                    reset
                  </button>
                </div>
              </div>
              <div className="settings-row">
                <span className="label">hotkey</span>
                <strong>ctrl + shift + c</strong>
                <span className="hint">copy latest phone clipboard</span>
              </div>
              <div className="settings-row">
                <span className="label">peer</span>
                <strong className="truncate">
                  {state.peer?.ip || 'none'}
                </strong>
                <span className="hint">{state.connected ? 'paired' : 'not paired'}</span>
              </div>
              <div className="settings-row">
                <span className="label">docs</span>
                <strong>local guides</strong>
                <span className="hint">architecture · protocol · setup</span>
              </div>
            </div>
            <div className="settings-actions">
              <button type="button" onClick={() => sendCommand({ action: 'OPEN_DOCS' })}>
                open docs
              </button>
              <button
                type="button"
                disabled={!state.clipboardPreview}
                onClick={() => sendCommand({ action: 'COPY_MOBILE_CLIPBOARD' })}
              >
                copy clip
              </button>
              <button
                type="button"
                className="solid"
                disabled={!state.connected}
                onClick={() => sendCommand({ action: 'PING' })}
              >
                ping phone
              </button>
            </div>
          </motion.section>
        ) : (
          <motion.div
            key="home"
            className="home-stack"
            variants={stagger}
            initial="initial"
            animate="animate"
            exit={{ opacity: 0, y: 8 }}
          >
            <motion.header className="top" variants={fadeUp} transition={{ duration: 0.3 }}>
              <div className="brand-block">
                <div className="brand-title-row">
                  <span className="brand-icon" aria-hidden>
                    <img src="/icon.ico" alt="" draggable={false} />
                  </span>
                  <h1>JODU</h1>
                </div>
                <p className="brand-tag">pair</p>
              </div>
            </motion.header>

            <motion.section
              className="cell devices"
              aria-label="Devices on LAN"
              variants={fadeUp}
              transition={{ duration: 0.32 }}
            >
              <div className="devices-head">
                <span className="label">devices on lan</span>
                <span className="sub">
                  {state.pairStatus === 'outgoing'
                    ? 'waiting for accept…'
                    : state.connected
                      ? 'paired'
                      : state.pairStatus === 'accepted'
                        ? 'connecting…'
                        : 'tap to request pair'}
                </span>
              </div>
              {peers.length === 0 ? (
                <p className="devices-empty">scanning for phones…</p>
              ) : (
                <ul className="device-list">
                  <AnimatePresence initial={false}>
                    {peers.map((p, i) => {
                      const pending = state.outgoingPairDeviceId === p.deviceId
                      const paired = state.connected && state.peer?.deviceId === p.deviceId
                      return (
                        <motion.li
                          key={p.deviceId}
                          initial={{ opacity: 0, x: -8 }}
                          animate={{ opacity: 1, x: 0 }}
                          exit={{ opacity: 0, x: 8 }}
                          transition={{ delay: i * 0.04, duration: 0.25 }}
                          layout
                        >
                          <button
                            type="button"
                            className={`device-row ${paired ? 'paired' : ''} ${pending ? 'pending' : ''}`}
                            disabled={
                              state.connected ||
                              state.pairStatus === 'outgoing' ||
                              state.pairStatus === 'accepted'
                            }
                            onClick={() =>
                              sendCommand({ action: 'PAIR_REQUEST', value: p.deviceId })
                            }
                          >
                            <span className="device-dot" aria-hidden />
                            <span className="device-meta">
                              <strong className="truncate">{p.deviceName}</strong>
                              <span className="sub truncate">
                                {p.ip} · {p.role}
                              </span>
                            </span>
                            <span className="device-action">
                              {paired ? 'paired' : pending ? '…' : 'pair'}
                            </span>
                          </button>
                        </motion.li>
                      )
                    })}
                  </AnimatePresence>
                </ul>
              )}
            </motion.section>

            <motion.main className="grid" variants={fadeUp} transition={{ duration: 0.34 }}>
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
                  <strong className="truncate" title={ssid}>
                    {ssid}
                  </strong>
                </div>
                <motion.div
                  className="meter"
                  style={{ '--level': `${battery ?? 0}%` } as CSSProperties}
                  aria-hidden
                  layout
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
                  <motion.button
                    type="button"
                    className="play"
                    disabled={!state.connected}
                    whileTap={{ scale: 0.92 }}
                    whileHover={state.connected ? { scale: 1.04 } : undefined}
                    onClick={() =>
                      sendCommand({
                        action: 'MEDIA',
                        value: playing ? 'PAUSE' : 'PLAY',
                      })
                    }
                    aria-label={playing ? 'Pause' : 'Play'}
                  >
                    {playing ? 'II' : '▶'}
                  </motion.button>
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

              <motion.section
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
                animate={dragging ? { scale: 1.01 } : { scale: 1 }}
                transition={{ type: 'spring', stiffness: 400, damping: 30 }}
              >
                <span className="label">files</span>
                <strong>drop to phone</strong>
                <span className="sub">phone can send back to this pc · downloads</span>
                <label className="pick">
                  browse
                  <input
                    type="file"
                    multiple
                    onChange={(e) => void onDrop(e.target.files)}
                  />
                </label>
                {(barPercent != null) ? (
                  <div
                    className="transfer-bar"
                    role="progressbar"
                    aria-valuenow={barPercent}
                    aria-valuemin={0}
                    aria-valuemax={100}
                  >
                    <span style={{ width: `${barPercent}%` }} />
                  </div>
                ) : null}
                <AnimatePresence>
                  {transferNote ? (
                    <motion.span
                      className="note"
                      initial={{ opacity: 0, y: 4 }}
                      animate={{ opacity: 1, y: 0 }}
                      exit={{ opacity: 0 }}
                    >
                      {transferNote}
                    </motion.span>
                  ) : null}
                </AnimatePresence>
              </motion.section>

              <section className="cell tools">
                <motion.button
                  type="button"
                  className="solid"
                  disabled={!state.connected}
                  whileTap={{ scale: 0.96 }}
                  onClick={() => sendCommand({ action: 'PING' })}
                >
                  ping
                </motion.button>
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
            </motion.main>

            <motion.footer className="foot" variants={fadeUp}>
              <span>{state.connected ? `paired · ${device}` : 'waiting'}</span>
              {state.peer?.ip ? (
                <>
                  <span className="dot-sep" />
                  <span>{state.peer.ip}</span>
                </>
              ) : null}
            </motion.footer>
          </motion.div>
        )}
      </AnimatePresence>
    </motion.div>
  )
}
