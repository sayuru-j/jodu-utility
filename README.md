# JODU

Lightweight zero-cloud phone ↔ PC bridge over local Wi-Fi.

```
JODU/
├── android/   # Kotlin Android app
└── desktop/   # .NET 10 + WebView2 host + Vite React UI
```

## Desktop

### Prerequisites
- .NET 10 SDK
- Node.js 20+
- WebView2 runtime (usually preinstalled on Windows 11)

### Develop UI (Vite)
```bash
cd desktop/ui
npm install
npm run dev
```

Then run the desktop host in Debug — it prefers `http://localhost:5173` when available.

### Build & run
```bash
cd desktop
dotnet build
dotnet run
```

The MSBuild target runs `npm run build` for the React UI and packs `ui/dist` into the app output. WebView2 loads it via the `jodu.local` virtual host.

### Features
- System tray + minimize-to-tray
- Global hotkey `Ctrl+Shift+C` → copy latest phone clipboard
- UDP discovery (`19283`) + WebSocket hub (`19284`) + file HTTP receiver (`19285` → `%USERPROFILE%\Downloads`)
- OTP toasts with **Copy Code**
- React UI: phone status, media controls, drag-and-drop upload, ping

### URL ACL (LAN listen)
If the phone cannot connect, allow HttpListener prefixes once (admin PowerShell):

```powershell
netsh http add urlacl url=http://+:19284/ user=Everyone
netsh http add urlacl url=http://+:19285/ user=Everyone
```

## Android

### Prerequisites
- Android Studio Ladybug+ / JDK 17
- Device or emulator on the same Wi-Fi as the PC

### Open & run
1. Open `android/` in Android Studio
2. Let Gradle sync
3. Run on a physical device (recommended for notification + media session access)
4. Grant notification listener access for OTP parsing
5. Keep the foreground service notification active

### Ports
| Role | Port |
|------|------|
| UDP discovery | 19283 |
| Desktop WebSocket | 19284 |
| Desktop file HTTP | 19285 |
| Android file HTTP | 19286 |

## Protocol

All WebSocket frames:

```json
{
  "type": "CLIPBOARD_UPDATE | TELEMETRY | OTP_DETECTED | MEDIA_CONTROL | MEDIA_STATE | PING_DEVICE | DISCOVERY",
  "payload": {}
}
```
