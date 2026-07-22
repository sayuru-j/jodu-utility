# Style reference

JODU UI follows the **Nothing OS–inspired** design system from **dasa-utility** (`C:\Users\Sayuru at Fleximal\Desktop\dasa-utility`).

## Source

| Platform | Reference |
|----------|-----------|
| Desktop | `dasa-utility/src/dasa-ui/src/index.css` |
| Native toasts | `dasa-utility/src/DASA.Host/Services/MoveNotificationOverlayService.cs` |
| Android | Same token set in `android/app/src/main/res/values/colors.xml` |

## Tokens (shared)

| Token | Hex | Usage |
|-------|-----|-------|
| Surface | `#0a0a0a` | App background |
| Surface alt | `#111111` | Cards, cells |
| Surface elevated | `#161616` | Hover |
| Stroke | `#1e1e1e` | Borders |
| Stroke strong | `#2a2a2a` | Strong borders |
| Text | `#f5f5f5` | Primary text |
| Text secondary | `#888888` | Body hints |
| Text tertiary | `#555555` | Mono labels |
| Accent | `#2dd4bf` | JODU link / paired (dasa uses `#d71921`) |
| Success | `#4ade80` | Connected / paired dot |
| Titlebar hover | `#1a1a1a` | Window controls |
| Close hover | `#e81123` | Close button |

## Typography

- **Sans:** Inter, Segoe UI — headings, body
- **Mono:** IBM Plex Mono, Consolas — labels (`10px`, `letter-spacing: 0.12em`, uppercase)

## Radii & controls

- Cards / cells: **6px**
- Buttons / inputs: **4px**
- Title bar height: **32px**
- Primary button: white fill on black (`#f5f5f5` on `#0a0a0a`)

## JODU mapping

| dasa-utility | JODU desktop | JODU Android |
|--------------|--------------|--------------|
| `index.css` `@theme` | `desktop/ui/src/index.css` | `values/colors.xml` |
| `.nothing-card` | `.cell`, `.device-row` | `@drawable/bg_cell` |
| `.nothing-label` | `.label` | `@style/Widget.Jodu.Label` |
| `.nothing-btn-primary` | `.solid` buttons | `@style/Widget.Jodu.Button.Solid` |
| Toast overlay | `NotificationPopupService.cs` | — |
