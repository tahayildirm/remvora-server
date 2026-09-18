# Remvora protocol v1

All signaling messages are UTF-8 JSON text frames of at most 65,536 bytes. Required fields: `protocolVersion:1`, nonempty UUID `messageId`, UTC ISO8601 `timestamp`, nullable UUID `sessionId`, `type`, and object `payload`. Clock skew tolerance is two minutes. Each peer allows at most 240 messages/minute and a bounded replay-ID set. Oversized, binary, invalid, misrouted or duplicate frames terminate signaling.

## Identity

P-256 ECDSA, SHA-256, public key encoded as base64 DER SubjectPublicKeyInfo. Signatures use base64 64-byte IEEE P1363 r||s, not ASN.1 DER signatures. Challenge bytes are UTF-8 `remvora:v1:{purpose}:{deviceUuid}:{randomHexNonce}`. Purpose is `activate` or `connect`. Nonces expire after 60 seconds and can be consumed once with optimistic concurrency protection.

## Agent connection

Connect to `/ws/agent` over WSS. Within ten seconds send `agent.authenticate`, no sessionId, payload `{challengeId,signature}`. The server responds `agent.connected`. Send `agent.heartbeat` every twenty seconds. Reauthentication is required on reconnect. Server revocation checks run every five seconds; losing signaling causes agent remote sessions to close.

## Remote session

A cookie-authenticated browser POSTs `{deviceId,kind:"Terminal"|"Desktop"}` to `/api/v1/remote-sessions`. The response contains a 60-second one-time ticket. Connect to `/ws/browser` with the exact allowed Origin and cookie; do not place credentials in URLs. First message is `session.request` with the returned sessionId and `{token}`. The server atomically consumes the ticket and sends an authorized `session.request` to the bound agent with `{kind,expiresAt}`. Agent answers `session.accept` or `session.reject`.

The browser sends `webrtc.offer` with `{type:"offer",sdp}` after ICE gathering. Agent replies `webrtc.answer` with `{type:"answer",sdp}` after ICE gathering. This implementation uses complete SDP instead of trickle ICE; the broker reserves `webrtc.iceCandidate` but clients currently use non-trickle negotiation. Session IDs cannot be used to route to another device. `session.close` ends the session and is audited.

## Terminal data channel

Label: `remvora.terminal.v1`, reliable ordered channel. Binary messages contain raw PTY bytes; browser input frames must be at most 16 KiB. Agent output chunks are at most 8 KiB. Text messages are control JSON: `{type:"resize",cols:80,rows:24}`. Rows clamp to 1..300 and columns to 1..500. Unknown controls do not execute commands. Bounded queues and buffered-byte limits prevent unbounded output buffering. Shell executable paths are local configuration; commands are never concatenated into process launch strings.

## Desktop

H.264 video track with `packetization-mode=1`. Label `remvora.input.v1` carries input JSON limited to 1 KiB. Supported controls: `move` with normalized x/y in 0..1; `down`/`up` with button 0/1/2; `keyDown`/`keyUp` with browser key names; `scroll` with signed delta. Agent uses an allowlist for named keys and supports single Unicode characters. Local desktop capability must be explicitly enabled and OS screen/accessibility permissions granted. Clipboard synchronization is intentionally unavailable in this release.

No TURN dependency. Optional STUN configuration uses `WebRtc:StunServers` on the server and `REMVORA_STUN_URL` on the agent. Never expose a failed connection as a successful remote session. Future relay transport must retain the same authorization boundary.
