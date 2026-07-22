# JODU

**JODU** (*pair* in Sinhala) — a lightweight **zero-cloud** phone ↔ PC bridge over local Wi-Fi.

A **hobby project**, built for fun. Use it, fork it, break it, improve it — contributions welcome.

**Author:** [Sayuru .J Silva](https://github.com/sayuru-j) · **Repository:** [sayuru-j/jodu-utility](https://github.com/sayuru-j/jodu-utility)

---

## Features

- **LAN discovery & pairing** — both sides list devices; either can send a pair request
- **Clipboard sync** — phone ↔ desktop over WebSocket
- **OTP forwarding** — regex parse from notifications → desktop toast + hotkey copy
- **Notification mirror** — phone status-bar alerts as right-edge desktop popups (with thumbnail + tone)
- **Media control** — play / pause / skip / volume from desktop
- **File transfer** — drag-and-drop desktop → phone; send from Android → desktop Downloads
- **Phone telemetry** — battery, charging, Wi-Fi SSID on desktop
- **Ping** — alert tone on the phone from desktop
- **Tray-first desktop** — custom title bar, Nothing OS–inspired UI (see [dasa-utility](https://github.com/sayuru-j/dasa-utility) style reference)

## Screenshots

<p align="center">
  <img src="docs/media/desktop.png" alt="JODU desktop — paired" width="720" />
  <br />
  <em>Desktop — paired with phone, status, media, and files</em>
</p>

<p align="center">
  <img src="docs/media/android.png" alt="JODU Android — bridge on" width="320" />
  <br />
  <em>Android — bridge on, paired desktop, file send</em>
</p>

## Architecture

```
JODU/
├── android/         # Kotlin foreground service + notification listener
├── desktop/         # .NET 10 WinForms + WebView2 + Vite React UI
├── docs/            # Architecture, protocol, guides
└── run-desktop.ps1  # Build & launch desktop
```

**Channels:**

| Channel | Port | Purpose |
|---------|------|---------|
| UDP discovery | `19283` | Peer advertisement, pair request/response |
| WebSocket | `19284` | Clipboard, telemetry, OTP, notifications, media, ping, transfer progress |
| HTTP (desktop) | `19285` | Phone → PC file upload |
| HTTP (Android) | `19286` | Desktop → phone file upload |

See [docs/architecture.md](docs/architecture.md) and [docs/protocol.md](docs/protocol.md).

## Requirements

| Component | Version |
|-----------|---------|
| Desktop OS | Windows 10/11 x64 |
| [.NET SDK](https://dotnet.microsoft.com/download) | 10.x (to build desktop) |
| [Node.js](https://nodejs.org/) | 20+ (to build UI) |
| [WebView2 Runtime](https://developer.microsoft.com/microsoft-edge/webview2/) | Evergreen |
| Android | 8+ (API 26), same Wi-Fi as PC |
| Android Studio | To build/install the phone app |

## Quick start (development)

### 1. Desktop

From repo root:

```powershell
.\run-desktop.ps1 -Dev
```

This starts Vite on `http://localhost:5173` and the desktop host in Debug (hot reload).

Release build without dev server:

```powershell
.\run-desktop.ps1
```

### 2. Android

1. Open `android/` in Android Studio.
2. Install on a device on the **same Wi-Fi** as the PC.
3. Turn **bridge on**.
4. Enable **notification access** (OTP, media sessions, notification mirror).
5. Tap a desktop in the LAN list to pair (or accept a pair request from desktop).

Closing the desktop window hides to the **tray**; use tray **Exit** to quit fully.

## Production build

From repo root:

```powershell
.\run-desktop.ps1          # Release build + run
```

Manual publish and zip steps: **[docs/PRODUCTION.md](docs/PRODUCTION.md)**.

Android: build signed/debug APK in Android Studio.

## Local data

| Path | Purpose |
|------|---------|
| `%LocalAppData%\JODU\device-id.txt` | Stable desktop device id |
| `%USERPROFILE%\Downloads\` | Files received from phone |
| Android `SharedPreferences` | Bridge toggle, device id |
| Android Downloads | Files received from desktop |

All data stays on-device / LAN — **no cloud backend**.

## Protocol summary

| Type | Direction | Purpose |
|------|-----------|---------|
| `DISCOVERY` | UDP | Peer advertisement |
| `PAIR_REQUEST` / `PAIR_RESPONSE` | UDP | Pair handshake |
| `CLIPBOARD_UPDATE` | either | Clipboard text |
| `TELEMETRY` | phone → desktop | Battery, Wi-Fi |
| `OTP_DETECTED` | phone → desktop | Parsed OTP |
| `NOTIFICATION` | phone → desktop | Notification mirror (+ optional image) |
| `MEDIA_CONTROL` / `MEDIA_STATE` | both | Playback control |
| `FILE_TRANSFER` | either | Transfer progress |
| `PING_DEVICE` | desktop → phone | Alert tone |

Full spec: [docs/protocol.md](docs/protocol.md).

## UI style

Nothing OS–inspired tokens (Inter, IBM Plex Mono, dark surfaces) — aligned with [dasa-utility](https://github.com/sayuru-j/dasa-utility). See **[docs/style-reference.md](docs/style-reference.md)**.

## Troubleshooting

| Issue | Fix |
|-------|-----|
| Phone never appears on desktop | Same Wi-Fi, not guest network; allow firewall for private LAN |
| Pair request not showing | Bridge on (Android); restart desktop after rebuild |
| White screen | Run `.\run-desktop.ps1 -Dev` or build UI (`npm run build` in `desktop/ui`) |
| `dotnet build` — file locked | Quit JODU from system tray |
| OTP / notifications missing | Enable notification listener on Android |
| File transfer fails | Confirm paired; desktop → phone uses port `19286` on phone IP |
| Media controls idle | Start playback on phone; notification access may be required |

More detail: [docs/troubleshooting.md](docs/troubleshooting.md).

## Documentation

| Doc | Description |
|-----|-------------|
| [docs/README.md](docs/README.md) | Documentation index + media guide |
| [Architecture](docs/architecture.md) | System design |
| [Protocol](docs/protocol.md) | Message contracts |
| [Desktop](docs/desktop.md) | Windows host |
| [Android](docs/android.md) | Phone app |
| [Style reference](docs/style-reference.md) | UI tokens |
| [Production](docs/PRODUCTION.md) | Release guide |
| [Backlog](docs/BACKLOG.md) | Planned work |

## Contributing

PRs and issues welcome. Keep changes focused and don’t break the build.

1. Fork and branch.
2. Test desktop (`.\run-desktop.ps1 -Dev`) and Android on LAN.
3. Open a PR with a short description.
