# Operations and recovery

## Database selection

Set `Database__Provider` to `PostgreSQL`, `MySQL`, or `MariaDB`, provide `ConnectionStrings__Database` through protected deployment configuration, then run `--migrate` and the one-time `--bootstrap`. This chooses a fresh installation's engine; it does not convert an existing database.

## Backup and restore

Use the database vendor's matching client tools. `scripts/database_backup.py` supports all three engines and refuses to overwrite either a backup file or a populated restore target. Run logical backups against a consistent, transactional database. MySQL/MariaDB schema changes must be paused during `--single-transaction` dumps.

PostgreSQL credentials use standard PGHOST/PGPORT/PGUSER/PGPASSFILE or securely injected PGPASSWORD. Set REMVORA_PSQL, REMVORA_PG_DUMP and REMVORA_PG_RESTORE when tools are not on PATH. For MySQL/MariaDB, set REMVORA_DB_DEFAULTS_FILE to a mode-0600 native client option file with `[client]` user/password/host or socket; optionally set REMVORA_MYSQL and REMVORA_MYSQL_DUMP executable paths. Passwords are never passed as command-line arguments.

```sh
python3 scripts/database_backup.py backup --provider PostgreSQL --database remvora --file .backups/remvora.dump
# Provision a separate EMPTY target database using your database administrator.
python3 scripts/database_backup.py restore --provider PostgreSQL --database remvora_recovery --file .backups/remvora.dump
```

For MySQL/MariaDB substitute the provider and use a `.sql` output. Protect/encrypt backups with the organization's backup system and replicate them to service-specific storage. The tool produces native logical dumps; it does not claim encryption at rest. A failed MySQL/MariaDB import may leave a partially populated recovery database; inspect and reprovision that disposable target before retrying, never use a production database for recovery drills.

Back up the persistent Data Protection key ring together with the RSA PFX certificate and its separately protected password. Losing these prevents decryption of TOTP secrets. Restore them using the same application name (`Remvora`) and service file permissions. Stop writes for the cutover, verify migration history, tenant/device/user counts, TOTP login and fresh agent sessions, then change application configuration to the recovered database. Keep the old installation intact until acceptance completes.

Local recovery drills passed on PostgreSQL 17, MySQL 8.4 and MariaDB 11.4: device/user/migration counts matched and repeated restore into populated targets was rejected. This is not a substitute for production encrypted-key recovery and disaster-recovery drills.

## Session and credential retention

Users can list and revoke only their own browser sessions under `/api/v1/auth/sessions`. Live remote sessions recheck authorization every five seconds. Device disable interrupts authentication; re-enable preserves its existing key; revocation cannot be undone this way.

Hourly maintenance removes ephemeral challenges, user sessions and remote ticket records expired more than 24 hours ago. Remote sessions are bounded to at most four hours, so abandoned consumed tickets can safely expire too. Consumed enrollments for devices awaiting approval are preserved. Audit events, user accounts, API keys, devices and relationship history are not deleted by this job. Adopt an explicit organizational audit archival policy before long-term production use.

## Pagination and limits

Device, catalog, user, API-key and audit list routes accept `offset` (non-negative) and `limit` (1–500, default 100). Responses remain arrays, ordered with a unique ID tie-breaker; an empty/short page ends traversal. Concurrent insertions can move offset boundaries. The web panel exposes page navigation; assignment selectors fetch all catalog pages.

HTTP requests are limited to 120/minute per direct peer; login is additionally bounded to 30/minute globally. WebSockets share a 1,024-connection cap, 10-second authentication deadline, 64 KiB signaling messages, 240 frames/minute and bounded replay caches. Agent sockets expire after 75 seconds without an application frame. Set reverse-proxy connection limits, preserve the documented single-process topology and size limits for your deployment. The sample proxy currently shares one upstream peer IP; review trusted proxy forwarding and per-client edge limits for production.

`python3 scripts/load_smoke.py` is a bounded loopback-only rate-limit check (240 requests, 16 workers), not a capacity benchmark. Observed local result: 120 OK, 120 rate-limited, no 5xx. Independent soak, NAT and resource-exhaustion testing remain required.

## Provider lifecycle

As verified on 2026-09-16, [Microsoft's support table](https://learn.microsoft.com/en-us/ef/core/what-is-new/) lists EF Core 9 support through 2026-11-10. [Pomelo's stable release](https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql/releases) remains the EF9-compatible 9.0.0 line. Plan and validate a supported provider upgrade before that date; the .NET 10 runtime does not extend EF9's lifecycle.

## Device-group access

`PUT /api/v1/users/{id}/device-groups` accepts `{ "groupIds": null }` for all organization devices, an empty array for no devices, or at most 100 organization group IDs. Membership is exact; descendant groups are not automatically included. Owners cannot be restricted. A scoped user retains only permitted device operations (excluding creation) and enrollment management. Catalog/relationship administration, users, API keys and audit administration are denied to avoid scope escalation. `/auth/me` reports effective permissions and deviceGroups; the panel exposes the restriction editor.

Apply the additive `AddUserDeviceGroupScope` migration for the selected provider. Do not simply drop this column or downgrade to a server that ignores it: disable/revoke scoped accounts and active sessions before a reviewed rollback. Promote to Owner only with full organization access; promotion clears the group restriction.

## Reboot requests

`POST /api/v1/devices/{id}/reboot` requires `devices.reboot`, access to the active online device and `{ "deviceCode": "exact-confirmation" }`. Built-in Owner/Admin roles have this permission; API keys do not. The server audits request and correlated result. Agent local `--allow-reboot` is separately required. Accepted means the native command accepted/scheduled a reboot, not proof the device actually restarted. This development host was never rebooted by acceptance tests; the denial path was tested.
