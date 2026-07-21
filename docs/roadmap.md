# To implement

Planned features not yet built.

## Incoming calls (answer / decline)

Mirror phone incoming calls on the desktop so the user can **answer** or **decline** from the PC while paired.

### Goals

- Show an incoming-call popup on desktop when the phone rings (caller name / number when available).
- Actions: **Answer** and **Decline** that control the phone call.
- Auto-dismiss when the call ends, is answered on the phone, or is rejected.

### Likely approach

- Android: listen for incoming-call state (`TelephonyCallback` / `PhoneStateListener`, or notification listener for dialer apps) with required phone permissions.
- Protocol: new events such as `INCOMING_CALL` (ringing metadata) and `CALL_CONTROL` (`ANSWER` / `DECLINE`).
- Desktop: dedicated call popup (or extend notification toasts) with Answer / Decline wired to `CALL_CONTROL` over the existing WebSocket bridge.

### Notes

- Requires runtime permissions on Android (`READ_PHONE_STATE`, and on newer APIs call-related / notification access depending on the approach).
- OEM dialers vary; prefer Telecom / Telephony APIs over scraping notifications when possible.
- Keep LAN-only; no cloud call relay.
