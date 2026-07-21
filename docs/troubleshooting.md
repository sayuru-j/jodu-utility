# Troubleshooting

## Phone never links

1. Confirm both devices are on the same Wi-Fi (not guest/AP isolation).
2. Allow Windows Firewall for private networks for `Jodu.Desktop`.
3. Reserve HttpListener URLs if LAN bind fails (see [Desktop](desktop.md)).
4. Check desktop settings panel for advertised ports and peer IP.

## Desktop fails to start (`HttpListener` disposed)

Fixed by recreating listeners on bind fallback. Update to latest desktop build. If ports are occupied, free `19284` / `19285`.

## Build error: Vite hashed assets missing (`MSB3030`)

UI filenames change every Vite build. The project copies `ui/dist` **after** `npm run build`. Use:

```powershell
.\run-desktop.ps1 -Dev
```

which sets `SkipViteBuild=true` while Vite serves live UI.

## OTP not arriving

1. Enable notification access for JODU on Android.
2. Confirm the notification body contains an OTP-like keyword and 4–8 digit code.
3. Keep the foreground service notification running.

## Media controls do nothing

Android needs an active media session (music/podcast app playing). Notification listener permission can also be required for session discovery on some OEMs.

## File transfer fails

- Desktop → phone uses the phone HTTP port from discovery (`19286`).
- Phone → desktop uses desktop `19285`.
- Check peer IP in the settings panel and that HTTP cleartext is allowed on Android.
