# ADR 0002: Opaque cookie sessions and device identity
## Context
Browser storage exposed to JavaScript increases bearer-token theft risk. Device passwords do not provide proof of device key possession.
## Decision
Use 256-bit opaque tokens in __Host-remvora Secure, HttpOnly, SameSite=Strict cookies; store only SHA-256 token hashes, rotate atomically on refresh and validate each HTTP request against the database. Require exact Origin and a custom header for browser mutations. Use ASP.NET Identity PBKDF2-HMAC-SHA512 at 210,000 iterations and RFC 6238 TOTP with last-step replay protection. Protect TOTP secrets with ASP.NET Data Protection. Enrollment uses P-256 SPKI public keys and fixed-width P1363 signatures over purpose/device/nonce-bound challenges.
## Alternatives
JWTs need extra revocation handling; browser localStorage credentials are rejected. Argon2id was considered; the platform-provided versioned password hasher avoids an additional native cryptographic dependency and permits rehash upgrades.
## Consequences
Database availability is required for authentication. Deploy the panel and API under one origin. Persist and protect the Data Protection key ring; losing it breaks existing 2FA secrets. Agent private keys remain on the device.
## Security Considerations
Recovery codes are single-use and hashed. Challenges and token consumption use concurrency checks. TLS is mandatory for deployment. External API keys are limited to device scopes and cannot approve enrollment. Configure production key-ring encryption and backup outside the repository's tracked files.
