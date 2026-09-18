# Changelog

## Unreleased
- Added tenant-scoped device and catalog management, hashed API keys, opaque sessions, TOTP and signed enrollment.
- Added initial PostgreSQL migration and security integration tests.
- Production acceptance is pending; see docs/STATUS.md.
- Added user-selected PostgreSQL/MySQL/MariaDB providers, custom roles and authenticated WebSocket sessions.
- Verified real Chrome-to-Rust-agent WebRTC terminal acceptance.

### Operations completion — 2026-09-16
- Individual session management, paginated lists, device disable/re-enable and safe credential retention.
- Bounded WebSocket concurrency/replay/idle handling.
- Tested native backup/restore for PostgreSQL, MySQL and MariaDB.
- Fresh local HTTPS/WSS acceptance with encrypted persisted keys. See docs/changes/2026-09-16-operations-and-session-completion.md.

## 2026-09-16 — group access and reboot
- Exact device-group user restrictions and effective permissions, enforced during active sessions.
- Confirmed and audited reboot requests with bounded result correlation and local agent opt-in.
- Additive scope migrations and all-provider authorization tests.
