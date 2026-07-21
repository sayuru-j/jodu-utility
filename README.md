# JODU

Hobby project — a lightweight zero-cloud phone ↔ PC bridge over local Wi-Fi.

<p align="center">
  <img src="docs/media/desktop.png" alt="JODU desktop" width="720" />
</p>

<p align="center">
  <img src="docs/media/android.png" alt="JODU Android" width="320" />
</p>

```
JODU/
├── android/         # Kotlin Android app
├── desktop/         # .NET 10 + WebView2 + Vite React UI
├── docs/            # Architecture, protocol, guides
└── run-desktop.ps1  # Build & launch desktop
```

## Quick start

```powershell
.\run-desktop.ps1 -Dev
```

Open `android/` in Android Studio, run on a device on the same Wi-Fi, start the bridge, and enable notification access for OTP.

## Documentation

Full docs live in [`docs/`](docs/README.md):

- [Architecture](docs/architecture.md)
- [Protocol](docs/protocol.md)
- [Desktop](docs/desktop.md)
- [Android](docs/android.md)
- [Troubleshooting](docs/troubleshooting.md)
- [Roadmap](docs/roadmap.md)
