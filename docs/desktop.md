# Desktop

## Stack

- .NET 10 WinForms + WebView2
- Vite + React + TypeScript UI in `desktop/ui`
- UDP discovery, WebSocket hub, HTTP file receiver

## Prerequisites

- .NET 10 SDK
- Node.js 20+
- WebView2 runtime (Windows 11 usually includes it)

## Run

From repo root:

```powershell
.\run-desktop.ps1          # Release build + packaged UI
.\run-desktop.ps1 -Dev     # Vite on :5173 + Debug host
.\run-desktop.ps1 -NoBuild # Skip build
```

### Dev UI only

```powershell
cd desktop/ui
npm install
npm run dev
```

Debug desktop prefers `http://localhost:5173` when available; otherwise `https://jodu.local/` (packaged `ui/dist`).

## Features

- Custom title bar (settings, min, max, close → tray)
- Global hotkey `Ctrl+Shift+C` → copy latest phone clipboard
- OTP Windows toasts with **Copy Code**
- Phone status, media controls, drag-and-drop upload, ping

## Project notes

- `SkipViteBuild=true` skips production UI rebuild (used by `-Dev`)
- UI assets are copied to output **after** Vite build (avoids stale hashed filenames)
- WebView2 WPF assembly is excluded to prevent `WindowsBase` MSB3277

## LAN listen

If the phone cannot connect, reserve URL prefixes once (admin PowerShell):

```powershell
netsh http add urlacl url=http://+:19284/ user=Everyone
netsh http add urlacl url=http://+:19285/ user=Everyone
```

The host also tries binding the LAN IP first, which often works without ACL.
