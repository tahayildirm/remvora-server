# ADR 0003: User-selected PostgreSQL, MySQL or MariaDB
## Context
The user explicitly requires all three database engines as installation choices. Provider compatibility must be real rather than an unimplemented configuration switch.
## Decision
Keep the .NET 10 runtime and use the stable EF Core 9.0.20 provider family: Npgsql 9.0.4 and Pomelo 9.0.0. Use distinct context types and separate migration histories. Store permission/scope arrays with explicit JSON serialization in portable text columns and map timestamps to UTC DateTime. Bound indexed strings for MySQL/MariaDB index limits.
## Alternatives
The Oracle EF10 provider does not establish MariaDB support. An unreleased Pomelo EF10 build is not appropriate for a production-targeted application. Hiding missing engines behind the PostgreSQL provider would be incorrect.
## Consequences
The same integration suite runs on all three engines. Provider switches do not convert existing data. Preserve previous development migration source rather than deleting history. Reassess EF9 support and move all provider families together before production release.
## Security Considerations
Tenant filters, compound foreign keys, concurrency-token replay protection and scope checks are provider-independent. Test all engines after schema or credential changes. Do not embed database passwords.
## References
- https://www.nuget.org/packages/Pomelo.EntityFrameworkCore.MySql/9.0.0
- https://github.com/PomeloFoundation/Pomelo.EntityFrameworkCore.MySql
- https://www.nuget.org/packages/Npgsql.EntityFrameworkCore.PostgreSQL/9.0.4
