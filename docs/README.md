# JODU documentation

Documentation for **JODU** — a zero-cloud phone ↔ PC bridge over local Wi-Fi.

| Document | Description |
|----------|-------------|
| [../README.md](../README.md) | Project overview, features, dev setup, protocol summary |
| [architecture.md](architecture.md) | Components, discovery, pairing, data flow |
| [protocol.md](protocol.md) | WebSocket / UDP / HTTP contracts |
| [desktop.md](desktop.md) | .NET host, Vite UI, run scripts |
| [android.md](android.md) | Kotlin app, permissions, services |
| [style-reference.md](style-reference.md) | Nothing OS UI tokens (from dasa-utility) |
| [troubleshooting.md](troubleshooting.md) | Common setup and LAN issues |
| [PRODUCTION.md](PRODUCTION.md) | Production builds, releases, install |
| [BACKLOG.md](BACKLOG.md) | Planned fixes and features (not yet implemented) |

---

## Media assets

Screenshots live in [`docs/media/`](media/).

| File | Description | Used in |
|------|-------------|---------|
| [`desktop.png`](media/desktop.png) | Desktop UI — paired state, status, media, files | README screenshots |
| [`android.png`](media/android.png) | Android UI — bridge on, paired desktop | README screenshots |

### Adding new media

1. Export PNG at a consistent width (1280px or 1440px recommended).
2. Save to `docs/media/` with a clear name: `jodu-<view>.png`.
3. Update this table and the Screenshots section in [README.md](../README.md).
4. Keep file sizes reasonable; compress PNGs before committing.

---

## UI map (for screenshots)

| View | What to capture |
|------|-----------------|
| **Desktop — home** | LAN device list, battery/Wi-Fi, media controls, file drop zone |
| **Desktop — paired** | Title bar link status, paired phone row, telemetry populated |
| **Desktop — pair request** | Incoming pair banner with accept / decline |
| **Desktop — settings** | Hotkey, peer IP, docs link |
| **Android — bridge on** | Status, LAN desktops, bridge toggle |
| **Android — paired** | Paired desktop row, send file, notification access |
| **Desktop — notification toast** | Right-edge phone notification popup (optional separate crop) |

---

## Repo layout

```
JODU/
├── android/           # Kotlin Android client
├── desktop/           # .NET 10 + WebView2 + Vite React UI
├── docs/              # This documentation
│   └── media/         # Screenshots
├── run-desktop.ps1    # Build & launch desktop
└── README.md
```
