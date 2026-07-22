# Backlog

Planned improvements and features not yet implemented. Items here are documented for future work — not shipped yet.

---

## Incoming calls (answer / decline)

**Status:** To implement  
**Priority:** Medium

Mirror phone incoming calls on the desktop so the user can **answer** or **decline** from the PC while paired.

**Current behavior:** Phone calls are not surfaced on desktop.

**Expected behavior:**

- Show an incoming-call popup on desktop when the phone rings (caller name / number when available).
- Actions: **Answer** and **Decline** that control the phone call.
- Auto-dismiss when the call ends, is answered on the phone, or is rejected.

**Relevant areas (for implementation):**

- Android: `TelephonyCallback` / `PhoneStateListener`, or notification listener for dialer apps
- Protocol: `INCOMING_CALL`, `CALL_CONTROL` (`ANSWER` / `DECLINE`)
- Desktop: call popup or extend `NotificationPopupService`
- Permissions: `READ_PHONE_STATE` and related call APIs on Android

**Notes:**

- OEM dialers vary; prefer Telecom / Telephony APIs over scraping notifications when possible.
- Keep LAN-only; no cloud call relay.

---
