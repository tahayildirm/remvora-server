# Remvora Server — installation and administration

**Fresh installation: [Step-by-step database and first Owner setup](FIRST_INSTALL.en.md).**
[English](GUIDE.en.md) · [Türkçe](GUIDE.tr.md) · [Release checklist](PUBLIC_RELEASE.md)

## What it is, and who it is for

Remvora is self-hosted remote-device administration for IT teams, schools, kiosk fleets, small businesses, support technicians and home labs. A browser is the operator console, a .NET server controls identity/permissions and connections, and a Rust agent runs on each managed device. Use it for authorized troubleshooting, terminal commands, interactive desktop support, inventory organization and bounded file exchange. It is not a hidden monitoring tool, public RDP gateway, MDM suite or guaranteed replacement for every OS management feature. There is no native mobile manager: phones/tablets use the web panel.

Three separate repositories must be deployed together: **Remvora Server** (this repository), **Remvora Web** (Angular 21, Node.js 24 build), **Remvora Agent** (Rust). Do not point your private devices at somebody else’s demo server. Public source repositories: [Server](https://github.com/tahayildirm/remvora-server) · [Web](https://github.com/tahayildirm/remvora-web) · [Agent](https://github.com/tahayildirm/remvora-agent).

## Architecture and scope

The browser and agent establish authenticated HTTPS/WSS connections to the API. Automatic mode attempts WebRTC P2P first; when direct ICE connectivity fails the existing WSS server relays terminal/video/input/audio/file payloads. The panel shows the transport and connection progress. STUN discovers network candidates; it is not a relay. CGNAT alone does not prove P2P impossible: NAT filtering, hotspot behavior, UDP policy and candidate exchange also matter. Test the actual two networks. Built-in TURN provisioning is absent.

P2P media/data use WebRTC encryption. WSS relay is TLS-encrypted on each hop and is **not opaque end-to-end encryption against the server**. Relay bandwidth and latency consume server resources; hosting must allow long-lived WebSockets and adequate traffic. Video stays compressed; quality/FPS/bitrate can be adjusted and automatically adapted. A single API process owns live signaling state. Do not enable a multi-worker web garden, replicas or load balancing without implementing a distributed broker.

Implemented areas: organization-scoped users/devices/catalogs; groups/tags/contacts/locations; roles and device-group scopes; enrollment/approval/key authentication; API keys; TOTP and recovery codes; audit; terminal; desktop/input; permitted text clipboard; audio; restricted file transfer; device disable/revoke/permanent delete and explicitly enabled reboot. Capability support depends on agent OS and local flags. See the Agent guide for platform/runtime limits.

## Deployment requirements

- .NET 10 SDK for building; .NET 10 runtime/ASP.NET Core Hosting Bundle as appropriate for deployment.
- PostgreSQL, MySQL or MariaDB. Tested development baselines were PostgreSQL 17, MySQL 8.4 and MariaDB 11.4. Set the actual MySQL/MariaDB server version.
- A database and database user with migration privileges for installation, writable persistent Data Protection key directory, RSA PFX certificate/password to encrypt that key ring.
- HTTPS certificate trusted by browsers and agents, DNS and WebSocket upgrade support. PHP-only static/shared hosting cannot host this API.
- Prefer one public origin, e.g. `https://remvora.example`, with `/api/`, `/ws/`, `/health` routed to the API and the web build served at `/`. Separate same-site HTTPS subdomains can work with the exact allowed panel origin. Cross-site domains can lose Strict cookies; do not assume arbitrary domains work merely because CORS is enabled.

Private backend port must not be exposed unnecessarily. Configure `AllowedHosts` for API hostnames and `Security__AllowedOrigin` to the panel origin (no trailing slash). Proxy examples are in the Web repository. IIS: one worker process, .NET 10 Hosting Bundle, WebSocket Protocol, suitable idle timeout and permissions for the key directory. Configure only your application/site.

## Install the database and first Owner

There are **no default credentials** and no open self-registration. Hosting credentials, database credentials, agent OS credentials and Remvora users are different identities.

Set these through your service environment/secret provider; `.env.example` is a reference, not automatically loaded by .NET:

```text
Database__Provider=PostgreSQL
ConnectionStrings__Database=<your database connection string>
Database__ServerVersion=<actual MySQL/MariaDB version; omit for PostgreSQL>
Security__AllowedOrigin=https://remvora.example
AllowedHosts=remvora.example
Security__KeyRingPath=/protected/remvora/keys
Security__KeyRingCertificate=/protected/remvora/keyring.pfx
Security__KeyRingPassword=<PFX password>
ASPNETCORE_ENVIRONMENT=Production
```

Provider must be exactly `PostgreSQL`, `MySQL` or `MariaDB`. Example connection formats (replace all placeholders privately):

```text
Host=DB_HOST;Database=remvora;Username=DB_USER;Password=DB_PASSWORD;SSL Mode=VerifyFull
Server=DB_HOST;Database=remvora;User ID=DB_USER;Password=DB_PASSWORD;SslMode=VerifyFull
```

Use your database provider’s correct TLS/trust settings. A local database listener and a remote database do not necessarily have the same TLS configuration. Never publish a real connection string. Changing providers later does not convert stored data.

From this repository:

```sh
dotnet restore
dotnet build -c Release
dotnet run --project src/Remvora.Api --no-launch-profile -- --migrate
```

Migrations create/update schema, not the initial user. Set `REMVORA_ADMIN_EMAIL`, `REMVORA_ADMIN_PASSWORD` (8–256 characters; a unique long password is preferable) and `REMVORA_ORGANIZATION` in the process environment using a secret manager or non-echoing prompt. Then:

```sh
dotnet run --project src/Remvora.Api --no-launch-profile -- --bootstrap
```

Bootstrap creates one organization and one **Owner** only when the entire installation has no organization. It refuses an already initialized database. Save the printed organization ID in private operational records and clear bootstrap secrets from the environment. Do not rerun bootstrap for the second user, wipe tables to fix login, or reuse the database password as the Owner password.

Publish and start using persistent production settings:

```sh
dotnet publish src/Remvora.Api -c Release -o .artifacts/server
# On the deployment host, from the published directory:
dotnet Remvora.Api.dll
```

Configure your own process supervisor/IIS site; install the panel separately. Check `/health` and `/health/ready` over HTTPS. Readiness only proves database connectivity, not a successful end-to-end session. Production startup requires encrypted key-ring configuration; key material is not shipped in source. Windows details: [deployment guide](../deploy/windows/README.md).

## First login and subsequent users

1. Open your panel; use the bootstrap email/password. The login form no longer requires an organization ID. Supply a TOTP/recovery code only when enabled.
2. In **Security**, enable TOTP and store the one-time recovery codes privately. Check server/device clocks if codes fail.
3. In **Users**, choose create/add, enter the colleague’s email, initial password (8–256) and a permitted role. Send the initial credential through a private channel; no invitation email delivery or public signup is implemented.
4. The colleague signs in, opens **My profile / Organization**, and changes their password with their current password. Profile editing supports email/password; do not assume arbitrary personal-profile fields exist.
5. Owner/Admin/Operator/Viewer and custom roles control actions. Use the permissions shown in the role editor as the authority. Admin cannot grant rights it does not hold or create an Owner; an Owner can create another Owner. The last Owner cannot be demoted.
6. Device-group scope `null` means all organization devices, `[]` means none, listed group IDs mean those exact groups (not automatic descendants). Owners cannot be scoped. Scoping also removes administrative operations that could bypass the scope.
7. Owner can see the organization’s user emails, roles, MFA status, lockout state, device scope and active-session counts; edit email, set a new password, unlock and revoke sessions. Existing passwords, hashes, MFA secrets and recovery codes are not displayed, including to Owner.

Organization selection in the profile lists eligible existing accounts after password verification; changing organization requires its own authentication/MFA. This is **not** a new-organization creation wizard or a global cross-tenant superadmin. The bootstrap CLI supports only the first organization; additional organization provisioning is not exposed as a general public UI/CLI flow in this version.

## Add the first device

In Web: create device → issue enrollment token → on the device run Agent `enroll` with `REMVORA_ENROLLMENT_TOKEN` → approve pending enrollment in Web → run Agent `activate` → start Agent with selected capabilities. Keep the same private state and OS account. See the Agent guide for exact commands. Enrollment approval is not the same as being online. Online/Busy/Offline/Unknown reflects live signaling; a stale `Active` enrollment label is not proof of reachability.

Disable is reversible and keeps identity. Revocation withdraws trust; reenrollment is required. Permanent delete is separate, protected by two panel confirmations, and must be treated as irreversible; audit history can remain. Never use delete as a connectivity fix.

## Operations, backup and updates

Back up the selected database, encrypted key ring, RSA PFX, protected certificate password and operator configuration consistently. Store server backups in this service’s protected backup location, not the static web folder. Test recovery into an isolated instance, verifying login/MFA, users/devices and new remote sessions. Losing encryption keys can make protected MFA data unreadable. Preserve each agent’s private identity separately; do not clone it between devices.

For upgrades: read release notes → back up → stop/drain this API → deploy compatible build → run `--migrate` for the configured provider → restart → verify health, login, device presence and actual terminal/desktop. Never bootstrap again on upgrade. Database downgrade is not automatically safe; restore compatible schema and application together if needed. API restarts disconnect active sessions. Static web update and agent binaries have separate deployment procedures.

Do not log request bodies, terminal content, tokens, cookies or recovery codes. Audit entries record management actions, not a promised complete screen/command recording system. Scope API keys narrowly; reveal/copy their token at creation, keep it privately, expire/revoke when unused. User sessions default to 12h; remote sessions to 1h; see `.env.example` and SecurityPolicy for configurable bounds. Keep OS, .NET, EF providers and native dependencies serviced.

## Troubleshooting and limits

| Symptom | Check |
|---|---|
| Cannot reach server | DNS/TLS, API process, `/health/ready`, correct panel API origin, exact CORS origin, same-site cookies |
| Login rejected | Correct Remvora account, bootstrap success, 8–256 password, 15-minute lockout after repeated failures, MFA/clock |
| Approved but offline | Agent service/logs, state identity, HTTPS trust, WSS upgrade, enabled device |
| P2P fails | Actual candidate exchange and UDP/NAT on both networks; use the reported WSS fallback, not a guess from ISP name |
| Relay slow | Both uplinks, latency/loss, hosting bandwidth/CPU, lower adaptive video quality; relay adds a server hop |
| Black desktop/no input | Active graphical session, supported display backend, local capture/input permissions and `--allow-desktop` |
| Session busy | Close the previous session; initial agent supports one active remote session at a time |
| Migration failure | Correct provider/version, connection, schema permissions and existing migration history; do not delete production tables |
| All Owners inaccessible | No default backdoor/reset account; use a tested restore or a reviewed administrative recovery procedure |

No built-in mail reset, guaranteed seamless network handover, distributed HA, unattended update discovery, native file clipboard or secure OS attention sequence support is promised. Unsigned development packages and selected real-device successes are not a complete production certification. See [status](STATUS.md).

## Development, license and releases

Run `dotnet build`, domain tests and integration tests against a **separate disposable database** using `REMVORA_TEST_DATABASE` and `REMVORA_TEST_PROVIDER`. Never use production for tests. API routes are rooted at `/api/v1`; development OpenAPI is `/openapi/v1.json`. Permission checks remain server-side.

MIT permits use/modification/distribution including commercial use while retaining the license notice, without warranty. Dependencies retain their own licenses. Hosting, bandwidth, support, certificates and third-party services are not included. Read LICENSE, THIRD_PARTY_NOTICES, SECURITY and PUBLIC_RELEASE before publishing.

## Practical secret entry and key-ring certificate example

For a manual Linux installation, the following **Bash** prompts avoid putting secret values literally into the command history. Run in the same process environment used for migration/bootstrap; configure a persistent service secret provider separately for normal operation:

```bash
read -r -p 'Initial Owner email: ' REMVORA_ADMIN_EMAIL
read -r -s -p 'Initial Owner password: ' REMVORA_ADMIN_PASSWORD
printf '\n'
read -r -p 'Organization name: ' REMVORA_ORGANIZATION
export REMVORA_ADMIN_EMAIL REMVORA_ADMIN_PASSWORD REMVORA_ORGANIZATION
# Run the bootstrap command from the installation section.
# After successful bootstrap:
unset REMVORA_ADMIN_EMAIL REMVORA_ADMIN_PASSWORD REMVORA_ORGANIZATION
```

Use a separately protected RSA PFX for Data Protection; it is **not your public HTTPS certificate**. Your certificate-management system can supply it. If OpenSSL is installed, an example generation procedure in a private service-owned directory is:

```sh
umask 077
openssl req -x509 -newkey rsa:3072 -keyout keyring.key -out keyring.crt -days 3650 -subj /CN=Remvora-DataProtection
openssl pkcs12 -export -inkey keyring.key -in keyring.crt -out keyring.pfx
```

OpenSSL prompts for private-key and export passwords; do not disable encryption. Point Security__KeyRingCertificate at the resulting PFX and provide its export password through Security__KeyRingPassword. Restrict ACLs to the service/administrators, back up this material privately, and never place it in a static web directory or release ZIP. Preserve older keys/certificates needed to decrypt existing data when designing rotation. HTTPS still needs its own browser/agent-trusted certificate.

## Per-device Linux terminal sudo/su policy

The device list's **Terminal sudo/su** setting persists in the database and defaults to off. `devices.terminalPolicy` manages it; `devices.terminalElevation` permits elevated-capable terminal sessions on enabled devices. Owner/Admin have both; custom roles may receive them separately. Terminal access alone does not grant sudo/su permission. Tenant/device-group scope and audit logging apply.

Linux only; agent 0.3.7+ is required. One-time local setup requires `--allow-terminal-privilege-escalation` and service `NoNewPrivileges=false`; routine changes then happen in the panel. Restricted sessions use `setpriv --no-new-privs`. This does not grant root or passwordless sudo: normal Linux permissions/passwords apply. Elevated-capable requests without local permission are explicitly rejected. Old agents lacking policy acknowledgement are rejected for terminal sessions; update the agent first.

Saving closes active terminal sessions; the device control connection and desktop sessions stay online. New sessions use the updated policy. The watchdog also disconnects a terminal when its elevation permission is revoked. Previously started privileged processes or system changes are not undone. This is not a complete OS sandbox and does not restrict someone already controlling the device's OS account.
