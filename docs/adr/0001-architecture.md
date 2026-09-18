# ADR 0001: Component-owned repositories and clean architecture
## Context
The user requires APIs under Desktop/WebApis, websites under Desktop/WebPages, and device software under Desktop/DesktopApps.
## Decision
Remvora API owns its C# solution, tests, migrations, operational files and documentation. The Angular panel and Rust agent are separate sibling projects in the relevant categories. Domain has no framework dependencies; Application depends only on Domain. Infrastructure implements storage and identity. HTTP endpoints adapt services.
## Alternatives
A monorepo was proposed by the initial specification; the user's explicit folder instruction supersedes that layout.
## Consequences
Each repository needs independent CI and versioning. Protocol versions coordinate releases. No code or build artifacts are copied between the roots.
## Security Considerations
A request-scoped actor binds persistence to a verified organization. Credential resolution is the only explicitly bounded query-filter bypass.
