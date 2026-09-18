# Initial security and architecture review — 2026-09-16

Validated: tenant reads/updates/deletes, unknown permission denial, role scope checks, last-owner preservation, hashed API key lifecycle, TOTP replay, recovery-code replay, cookie rotation, Origin defense, signed enrollment approval and replay, one-time WebSocket authorization, provider migrations.

Corrected during review: outdated transitive OpenAPI/EF dependencies; EF version conflicts; a test-host configuration ordering issue; unguarded alternate SaveChanges overloads; protected production Data Protection key-ring requirement; desktop permission-failure teardown; browser acceptance-test approval race; macOS pointer movement/click ordering; bounded input queue overflow teardown and held mouse-button cleanup.

Remaining release gates:
- Independent security review and resource-exhaustion/load testing; operational sizing of per-client edge limits, challenge issuance and connection caps still needs production traffic testing. Hourly credential retention and bounded socket/replay limits are implemented.
- Windows/Linux/x64/ARM64 native runtime validation, Windows Service runtime acceptance, signed installers and Windows DPAPI/ACL runtime verification.
- Screen capture, H.264 video delivery and keyboard/mouse input passed real Chrome → Rust acceptance on this macOS ARM64 machine with OS-granted permissions. Repeat on supported Windows/Linux and other architecture/display configurations.
- TLS/reverse-proxy/shared-hosting deployment acceptance. A fresh local Production-mode HTTPS/WSS test passed with private CA verification and an encrypted key ring; public hosting and reverse-proxy acceptance remain unverified.
- Backup/restore passed for all three engines. Released-schema upgrade drills, encrypted key-ring disaster recovery and EF provider lifecycle review remain.
- Native clipboard read/write passed on macOS with explicit local permission and user-triggered actions; repeat across supported platforms.
- Native desktop input/display coordinate scaling across multi-monitor/HiDPI setups; locally selected display mapping requires broader platform testing.

Do not call this a production release until these gates are closed. Unsupported desktop access fails closed; no unsigned updater, hidden relay or password-based device backdoor is included.

Dependency scans: NuGet and npm reported no known vulnerabilities. The agent now uses WebRTC 0.17.2, removing the previous bincode dependency; no advisory suppression was added.

Follow-up review: restricted users cannot mutate group assignments or tenant administration to escape their scope. Exact group membership is enforced on reads and remote authorization; active sessions recheck restrictions. The owner cannot be restricted. Reboot requires a separate permission and confirmation, expires and has bounded correlation; enabled native reboot has not been exercised. Signed executable installation and failure rollback passed isolated tests but native service/ACL and power-loss recovery acceptance remains required.
