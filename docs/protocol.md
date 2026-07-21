# Protocol

## WebSocket envelope

Every frame is UTF-8 JSON:

```json
{
  "type": "EVENT_NAME",
  "payload": {}
}
```

### Event types

| Type | Direction | Purpose |
|------|-----------|---------|
| `DISCOVERY` | UDP both | Peer advertisement |
| `PAIR_REQUEST` | UDP either | Ask a specific device to pair |
| `PAIR_RESPONSE` | UDP either | Accept / decline pair (`accepted`) |
| `TELEMETRY` | phone → desktop | Battery, charging, Wi-Fi SSID |
| `CLIPBOARD_UPDATE` | either | Synced clipboard text |
| `OTP_DETECTED` | phone → desktop | Parsed OTP code |
| `NOTIFICATION` | phone → desktop | Phone status-bar notification mirror |
| `MEDIA_CONTROL` | desktop → phone | `PLAY` / `PAUSE` / `NEXT` / `PREVIOUS` / volume |
| `MEDIA_STATE` | phone → desktop | Title, artist, playing, volume |
| `PING_DEVICE` | desktop → phone | Trigger phone alert tone |

### Example payloads

**Telemetry**
```json
{
  "type": "TELEMETRY",
  "payload": {
    "batteryPercent": 76,
    "isCharging": true,
    "wifiSsid": "Home-LAN",
    "deviceName": "Pixel"
  }
}
```

**Clipboard**
```json
{
  "type": "CLIPBOARD_UPDATE",
  "payload": { "text": "hello", "source": "phone" }
}
```

**OTP**
```json
{
  "type": "OTP_DETECTED",
  "payload": { "code": "482193", "sender": "com.bank.app", "body": "..." }
}
```

**Notification**
```json
{
  "type": "NOTIFICATION",
  "payload": {
    "packageName": "com.whatsapp",
    "appName": "WhatsApp",
    "title": "Alex",
    "text": "On my way",
    "key": "0|com.whatsapp|1|null|10123",
    "postedAt": 1710000000000
  }
}
```

**Media control**
```json
{
  "type": "MEDIA_CONTROL",
  "payload": { "action": "PAUSE" }
}
```

## Ports

| Role | Port |
|------|------|
| UDP discovery | `19283` |
| Desktop WebSocket | `19284` |
| Desktop file HTTP | `19285` |
| Android file HTTP | `19286` |

## File transfer

`POST /upload` with raw body and header:

```
X-Filename: report.pdf
Content-Type: application/octet-stream
```

- Desktop saves to `%USERPROFILE%\Downloads`
- Android saves to `Environment.DIRECTORY_DOWNLOADS`
