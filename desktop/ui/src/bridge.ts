export type Telemetry = {
  batteryPercent: number
  isCharging: boolean
  wifiSsid?: string | null
  wifiConnected?: boolean
  wifiRssi?: number | null
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

export type FileTransfer = {
  fileName: string
  direction: string
  bytesTransferred?: number
  totalBytes?: number
  percent: number
  status: string
  error?: string | null
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
  transfer?: FileTransfer | null
  clipboardPreview?: string
  httpPort: number
  wsPort: number
  maximized?: boolean
  startWithWindows?: boolean
  autoConnectLastDevice?: boolean
  lastPeerDeviceName?: string | null
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

const ANDROID_FILE_HTTP = 19286

export function sendCommand(command: UiCommand) {
  const payload = JSON.stringify(command)
  if (window.chrome?.webview) {
    window.chrome.webview.postMessage(payload)
    return
  }
  console.debug('[jodu] command', command)
}

export function resolvePeerUploadBase(
  peer: Peer | null | undefined,
  peers: Peer[] | undefined,
  connected: boolean,
): string {
  const linked =
    peer?.ip
      ? peer
      : connected
        ? peers?.find((p) => p.role?.toLowerCase() === 'android' && p.ip)
        : undefined
  if (!linked?.ip) return ''
  const port =
    linked.httpPort > 0 && linked.httpPort !== 19285
      ? linked.httpPort
      : ANDROID_FILE_HTTP
  return `http://${linked.ip}:${port}`
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

export function uploadFiles(
  files: FileList | File[],
  peerBase: string,
  onProgress?: (fileName: string, percent: number, index: number, total: number) => void,
): Promise<void> {
  const list = Array.from(files)
  return list.reduce(async (prev, file, index) => {
    await prev
    await uploadOne(file, peerBase, (percent) => {
      onProgress?.(file.name, percent, index, list.length)
    })
  }, Promise.resolve())
}

function uploadOne(
  file: File,
  peerBase: string,
  onProgress?: (percent: number) => void,
): Promise<void> {
  return new Promise((resolve, reject) => {
    const xhr = new XMLHttpRequest()
    xhr.open('POST', `${peerBase}/upload`)
    xhr.setRequestHeader('X-Filename', file.name)
    xhr.setRequestHeader('Content-Type', file.type || 'application/octet-stream')
    xhr.upload.onprogress = (event) => {
      if (!event.lengthComputable) return
      const percent = Math.round((event.loaded * 100) / event.total)
      onProgress?.(percent)
    }
    xhr.onload = () => {
      if (xhr.status >= 200 && xhr.status < 300) resolve()
      else reject(new Error(`upload failed: HTTP ${xhr.status}`))
    }
    xhr.onerror = () => reject(new Error('upload failed'))
    onProgress?.(0)
    xhr.send(file)
  })
}
