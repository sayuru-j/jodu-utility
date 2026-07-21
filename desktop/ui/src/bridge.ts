export type Telemetry = {
  batteryPercent: number
  isCharging: boolean
  wifiSsid?: string | null
  deviceName?: string | null
}

export type MediaState = {
  title?: string | null
  artist?: string | null
  isPlaying: boolean
  volume: number
}

export type Peer = {
  deviceId: string
  deviceName: string
  role: string
  ip: string
  wsPort: number
  httpPort: number
}

export type IncomingPair = {
  deviceId: string
  deviceName: string
  ip: string
  role: string
}

export type AppState = {
  connected: boolean
  peer?: Peer | null
  peers?: Peer[]
  incomingPair?: IncomingPair | null
  outgoingPairDeviceId?: string | null
  pairStatus?: string
  telemetry?: Telemetry | null
  media?: MediaState | null
  clipboardPreview?: string
  httpPort: number
  wsPort: number
  maximized?: boolean
}

type UiCommand = {
  action: string
  value?: string
}

type WebViewHost = {
  postMessage: (message: string) => void
  addEventListener: (
    type: 'message',
    listener: (event: MessageEvent) => void,
  ) => void
  removeEventListener: (
    type: 'message',
    listener: (event: MessageEvent) => void,
  ) => void
}

declare global {
  interface Window {
    chrome?: {
      webview?: WebViewHost
    }
  }
}

export function sendCommand(command: UiCommand) {
  const payload = JSON.stringify(command)
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage(payload)
    return
  }
  console.debug('[jodu] command', command)
}

export function subscribeState(onState: (state: AppState) => void): () => void {
  const handler = (event: MessageEvent) => {
    try {
      const data =
        typeof event.data === 'string' ? JSON.parse(event.data) : event.data
      onState(data as AppState)
    } catch {
      // ignore
    }
  }

  if (window.chrome?.webview) {
    window.chrome.webview.addEventListener('message', handler)
    sendCommand({ action: 'GET_STATE' })
    return () => window.chrome?.webview?.removeEventListener('message', handler)
  }

  onState({
    connected: false,
    clipboardPreview: '',
    httpPort: 19285,
    wsPort: 19284,
    pairStatus: 'idle',
    peers: [
      {
        deviceId: 'demo1',
        deviceName: 'Pixel 8',
        role: 'android',
        ip: '192.168.1.42',
        wsPort: 19284,
        httpPort: 19286,
      },
    ],
    telemetry: {
      batteryPercent: 76,
      isCharging: true,
      wifiSsid: 'Home-LAN',
      deviceName: 'Pixel 8',
    },
    media: {
      title: 'Waiting for phone',
      artist: 'JODU',
      isPlaying: false,
      volume: 40,
    },
  })
  return () => undefined
}

export async function uploadFiles(files: FileList | File[], peerBase: string) {
  const list = Array.from(files)
  for (const file of list) {
    await fetch(`${peerBase}/upload`, {
      method: 'POST',
      headers: {
        'X-Filename': file.name,
        'Content-Type': 'application/octet-stream',
      },
      body: file,
    })
  }
}
