# Android

## Stack

- Kotlin, AndroidX, Material 3
- OkHttp WebSocket client
- NanoHTTPD file server
- Foreground service for persistent connectivity

## Prerequisites

- Android Studio (Ladybug+) / JDK 17
- Physical device on the same Wi-Fi as the PC (recommended)

## Run

1. Open `android/` in Android Studio
2. Gradle sync
3. Install on device
4. Start bridge from the app screen
5. Enable **notification listener** access for OTP parsing

## Services

| Component | Role |
|-----------|------|
| `JoduForegroundService` | Discovery, WebSocket, telemetry, clipboard, media, ping, calls |
| `IncomingCallMonitor` | Cellular ring state + Telecom answer/decline |
| `OtpNotificationListener` | Regex OTP extraction from notifications |
| `FileHttpServer` | Receives desktop uploads on port `19286` |
| `BootReceiver` | Restarts bridge after reboot |

## Permissions to expect

- Notifications (foreground service + OTP prompts)
- Notification listener (OTP / notification mirror)
- Phone state + answer phone calls (incoming call mirror)
- Call log (optional; improves caller number on some devices)
- Contacts (optional; shows caller name on the desktop popup)
- Network / Wi-Fi state
- Cleartext HTTP (LAN only; see `network_security_config.xml`)

## OTP matching

Looks for `\b\d{4,8}\b` when notification text also contains keywords such as `code`, `OTP`, `verification`.
