# Architecture

JODU is a **zero-cloud**, local Wi-Fi bridge between an Android phone and a Windows desktop.

## Components

```mermaid
flowchart LR
  phone[Android app]
  pc[Desktop host]
  ui[Vite React UI in WebView2]

  phone <-- UDP discovery 19283 --> pc
  phone <-- WebSocket 19284 --> pc
  phone <-- HTTP files 19285/19286 --> pc
  pc --- ui
```

| Piece | Role |
|-------|------|
| `desktop/` | Tray app, WebSocket server, file HTTP receiver, hotkeys, toasts |
| `desktop/ui/` | Single-page React UI (status, media, files, ping, settings) |
| `android/` | Foreground service, clipboard, OTP listener, media, telemetry, ping |

## Discovery

Both sides broadcast UDP JSON `DISCOVERY` packets on port **19283**.

- Desktop role: `desktop`
- Phone role: `android`

When the phone sees a desktop peer, it opens a WebSocket client to `ws://{ip}:{wsPort}/`.

## Runtime channels

1. **WebSocket** — clipboard, telemetry, OTP, media, ping
2. **HTTP POST `/upload`** — file streams (`X-Filename` header)
3. **UDP** — discovery only

## Desktop window shell

- Borderless WinForms host with custom React title bar
- Drag / min / max / close via WebView2 `postMessage`
- Close hides to tray; quit from tray menu
