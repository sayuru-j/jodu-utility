# Production & release guide

How to build, package, and distribute **JODU** for end users.

**Note:** JODU is a **hobby project** — no formal release pipeline yet. This guide matches the workflow used for similar desktop apps (see [dasa-utility](https://github.com/sayuru-j/dasa-utility) `docs/PRODUCTION.md`).

---

## Table of contents

1. [Pre-release checklist](#pre-release-checklist)
2. [Version numbering](#version-numbering)
3. [Production build — desktop](#production-build--desktop)
4. [Production build — Android](#production-build--android)
5. [Verify the build](#verify-the-build)
6. [Publishing to GitHub](#publishing-to-github)
7. [End-user installation](#end-user-installation)
8. [Local data & secrets](#local-data--secrets)
9. [Support matrix](#support-matrix)

---

## Pre-release checklist

Before tagging a release, confirm:

- [ ] `npm run build` succeeds in `desktop/ui`
- [ ] `dotnet publish -c Release` succeeds in `desktop/`
- [ ] `Jodu.Desktop.exe` runs on a clean Windows PC with WebView2 installed
- [ ] UI loads from packaged `ui/` (not white screen) — **do not** rely on Vite in production
- [ ] Tray icon, window icon, and taskbar icon display correctly
- [ ] UDP discovery lists phone and desktop on the same LAN
- [ ] Pair request → accept → WebSocket link works
- [ ] Clipboard, OTP, notification mirror, media, file transfer, ping smoke-tested
- [ ] Android APK built and installs; bridge toggle + notification access documented
- [ ] `README.md` and version strings updated
- [ ] Release notes drafted
- [ ] Screenshots in `docs/media/` still reflect the UI

---

## Version numbering

Version is defined in:

| Component | File |
|-----------|------|
| Desktop | `desktop/Jodu.Desktop.csproj` (`<Version>` if set) |
| Android | `android/app/build.gradle.kts` (`versionName`) |

Use [Semantic Versioning](https://semver.org/): `MAJOR.MINOR.PATCH`.

Keep the git tag aligned: `v0.1.0`.

---

## Production build — desktop

From the **repository root**:

```powershell
.\run-desktop.ps1
```

This runs a **Release** build (including Vite when `node_modules` exists) and launches the app.

### Manual publish

```powershell
cd desktop\ui
npm ci
npm run build

cd ..
dotnet publish Jodu.Desktop.csproj -c Release -r win-x64 --self-contained false -o .\publish
```

**Self-contained (no separate .NET install):**

```powershell
dotnet publish Jodu.Desktop.csproj -c Release -r win-x64 --self-contained true -o .\publish
```

### Package zip

```powershell
Compress-Archive -Path .\publish\* -DestinationPath .\Jodu-v0.1.0-win-x64.zip -Force
```

The publish folder should contain at minimum:

```
Jodu.Desktop.exe
ui/
  index.html
  assets/
  icon.ico
  notification.ogg
... (runtime dependencies)
```

---

## Production build — Android

1. Open `android/` in Android Studio.
2. **Build → Generate Signed Bundle / APK** (or debug APK for testing).
3. Install on a device on the same Wi-Fi as the desktop.

There is no Gradle wrapper in this repo — use Android Studio for builds.

---

## Verify the build

Run from the publish folder **without** Vite and **without** dev flags:

```powershell
cd desktop\publish
.\Jodu.Desktop.exe
```

Confirm:

1. App opens (no white screen).
2. Tray icon visible; close hides to tray.
3. Phone and desktop discover each other on LAN.
4. Pair and link complete; status tiles update.
5. Notification popups + tone on phone notifications (when listener enabled).

Test Android on a physical device on the same subnet.

### Release media

Attach assets from [`docs/media/`](media/) in GitHub Releases:

| Asset | File |
|-------|------|
| Desktop UI | `docs/media/desktop.png` |
| Android UI | `docs/media/android.png` |

See [docs/README.md](README.md) for capture notes.

---

## Publishing to GitHub

1. Bump version in project files.
2. Run [production build](#production-build--desktop).
3. Commit, tag (`v0.1.0`), push.
4. **Releases → Draft a new release** — upload `Jodu-v0.1.0-win-x64.zip`.
5. Attach APK separately or link Android Studio build instructions.

**Release notes template:**

```markdown
## JODU v0.1.0

Zero-cloud phone ↔ PC bridge over local Wi-Fi.

### Install (desktop)
1. Download `Jodu-v0.1.0-win-x64.zip`
2. Extract and run `Jodu.Desktop.exe`
3. Allow firewall on private networks

### Install (Android)
Build/install from `android/` via Android Studio; enable notification access.

### Requirements
- Windows 10/11 x64 + WebView2 Runtime
- .NET 10 Runtime (framework-dependent build)
- Android 8+ on same Wi-Fi

### Highlights
- LAN discovery + tap-to-pair
- Clipboard, OTP, notification mirror, media, files, ping
- Nothing OS–inspired UI (desktop + Android)
```

Optional [GitHub CLI](https://cli.github.com/):

```powershell
gh release create v0.1.0 .\Jodu-v0.1.0-win-x64.zip --title "JODU v0.1.0" --notes-file RELEASE_NOTES.md
```

---

## End-user installation

### Desktop

1. Download and extract the release zip.
2. Run `Jodu.Desktop.exe`.
3. Allow Windows Firewall for **private** networks.
4. Pair from either device (see [Architecture](architecture.md)).

### Android

1. Install the APK.
2. Turn **bridge on**.
3. Grant notification access (OTP, media sessions, notification mirror).
4. Grant storage access when prompted (incoming files → Downloads).
5. Tap a desktop in the LAN list to pair.

### Uninstall

- **Desktop:** Exit from tray → delete install folder. Optional: `%LocalAppData%\JODU\` (device id).
- **Android:** Uninstall app; disable notification listener if desired.

---

## Local data & secrets

### Never commit

| Item | Why |
|------|-----|
| User `%LocalAppData%\JODU\` | Device id, local state |
| Keystores / signing keys | Android release signing |
| `.env` files | May contain secrets |

### Desktop local paths

| Path | Purpose |
|------|---------|
| `%LocalAppData%\JODU\device-id.txt` | Stable desktop device id |
| `%USERPROFILE%\Downloads\` | Files received from phone |

### Network

- All traffic stays on LAN — no cloud relay.
- Cleartext HTTP/WebSocket on local ports only (see [Protocol](protocol.md)).

---

## Support matrix

| Environment | Supported |
|-------------|-----------|
| Windows 11 x64 | Yes (primary) |
| Windows 10 x64 | Yes |
| Android 8+ (API 26+) | Yes |
| macOS / Linux desktop | No |
| iOS | No |

---

## Quick reference

```powershell
# Dev (Vite + Debug host)
.\run-desktop.ps1 -Dev

# Release build + run
.\run-desktop.ps1

# Manual publish
cd desktop\ui; npm ci; npm run build
cd ..; dotnet publish Jodu.Desktop.csproj -c Release -r win-x64 -o .\publish
```
