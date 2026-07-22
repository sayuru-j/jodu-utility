# Backlog

Planned improvements and features not yet implemented. Items here are documented for future work — not shipped yet.

---

## Incoming calls — OEM dialer quirks

**Status:** Partially shipped  
**Priority:** Low

Core telephony path is implemented (`INCOMING_CALL` / `CALL_CONTROL`, desktop Answer/Decline popup). Remaining polish:

- Caller **display name** from contacts (needs `READ_CONTACTS`)
- Stronger number resolution on Android 12+ (TelephonyCallback no longer provides the ringing number)
- OEM-specific answer/decline reliability (some devices prefer `InCallService` / default-dialer role)

---
