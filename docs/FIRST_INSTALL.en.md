# First installation: shared hosting, database and first administrator

[English](FIRST_INSTALL.en.md) · [Türkçe](FIRST_INSTALL.tr.md) · [Home](../README.md)

This guide starts with an **empty installation** and ends with your first Owner login, additional users and a managed device. For upgrades, back up first and run migrations only: do not run bootstrap again.

## 1. Plan the installation and check hosting support

| Component | Example | Location |
|---|---|---|
| Server / API | `https://api.example.com` | Hosting capable of running .NET applications |
| Web panel | `https://rdp.example.com` | Static HTML/JS/CSS hosting |
| Agent | Connects to the API | Each managed Windows, Linux, macOS or Raspberry Pi device |
| Database | Provider-supplied host/port | PostgreSQL, MySQL or MariaDB |

**Compatible Windows shared hosting with IIS/Plesk can run Remvora; a dedicated VPS is not mandatory.** Check these requirements with your provider before purchasing a plan:

- .NET 10 Hosting Bundle / ASP.NET Core Module V2 and an application pool matching the published architecture (x64 here).
- HTTPS and long-lived WebSocket/WSS connections without restrictive proxy timeouts.
- A supported database and permission to create/alter tables and indexes in the dedicated Remvora database.
- Persistent private storage for the writable Data Protection key ring and its RSA PFX certificate.
- One API worker. Idle shutdown, recycling and CPU/traffic quotas affect active sessions.
- A way to execute **one-time migration/bootstrap commands**: console, SSH, PowerShell, a permitted Plesk scheduled task, or hosting support running them for your application.

FTP uploads alone do not create tables or the first account. There is no HTTP installation/password-reset endpoint. A scheduled-task menu does not guarantee executable permissions. PHP-only hosting cannot run this API; Linux shared hosting needs .NET process hosting and WSS too. Prefer API and panel subdomains under the same parent domain. Replace every example domain and placeholder below.

## 2. Create an empty database and its database user

### Plesk / hosting control panel

1. Select the API domain, open **Databases → Add Database** (labels vary by provider).
2. Select PostgreSQL, MySQL or MariaDB. MariaDB may appear under a MySQL menu; verify the actual engine.
3. Create a new, empty database such as `remvora`. Use the complete name shown by the host, including any account prefix.
4. Create a separate database user with a private password and access only to this database. Migration requires schema creation/change permissions.
5. Copy the host, port, full database name and username from connection details. `localhost` means the API's hosting machine, not your laptop.
6. For MySQL/MariaDB, run `SELECT VERSION();` in the database console/phpMyAdmin or ask the provider. Configure only the numeric version: `11.4.5-MariaDB` becomes `11.4.5`.

Do not manually create Remvora tables or reuse another application's database. Migration creates the schema in step 5. The database password is **not** your Remvora login password.

### SQL alternative for a database server you administer

Run these examples using an administrative database connection. Replace the password privately; do not commit credentials.

PostgreSQL:

```sql
CREATE ROLE remvora_app LOGIN PASSWORD 'REPLACE_WITH_UNIQUE_DB_PASSWORD';
CREATE DATABASE remvora OWNER remvora_app;
```

MySQL / MariaDB, with an API on the same machine:

```sql
CREATE DATABASE remvora CHARACTER SET utf8mb4 COLLATE utf8mb4_unicode_ci;
CREATE USER 'remvora_app'@'localhost' IDENTIFIED BY 'REPLACE_WITH_UNIQUE_DB_PASSWORD';
GRANT ALL PRIVILEGES ON remvora.* TO 'remvora_app'@'localhost';
```

For a remote API, restrict the user to its actual permitted connection source instead of `localhost`; do not expose the database generally. Shared hosts may prohibit SQL account/database creation; use their control panel instead.

## 3. Build and upload the two applications separately

On a development computer with .NET 10 SDK, Git and Python 3:

```sh
git clone https://github.com/tahayildirm/remvora-server.git
cd remvora-server
dotnet restore --locked-mode
dotnet publish src/Remvora.Api -c Release -r win-x64 --self-contained false -o .artifacts/windows-iis/api
python3 scripts/package-windows.py --api-origin https://api.example.com --panel-origin https://rdp.example.com --provider MariaDB
```

Change `--provider` for MySQL or PostgreSQL. Upload and extract `.artifacts/windows-iis/remvora-api-win-x64.zip` into the **API site**. Its `app_offline.htm` keeps this application in maintenance during setup. Generated configuration is a template, not working credentials.

Build the web panel in a separate checkout with Node.js 24/npm and Python 3:

```sh
git clone https://github.com/tahayildirm/remvora-web.git
cd remvora-web
npm ci
npm run build
python3 scripts/package-windows.py --api-origin https://api.example.com
```

Upload `dist/remvora-web-panel.zip` contents into the **panel site**. Node.js is needed to build, not to serve the static panel. Never place database passwords or API secrets there. See [Windows deployment](../deploy/windows/README.md) for IIS configuration.

## 4. Configure the production API

Use the hosting provider's protected configuration mechanism to edit `appsettings.Production.json` in the API publish directory. Confirm IIS blocks downloads of configuration, JSON and App_Data files. Environment variables may override JSON; `.env` is not loaded automatically.

MariaDB example; replace every placeholder and use your actual server version:

```json
{
  "AllowedHosts": "api.example.com",
  "Database": { "Provider": "MariaDB", "ServerVersion": "11.4.5" },
  "ConnectionStrings": {
    "Database": "Server=DB_HOST;Port=3306;Database=DB_NAME;User ID=DB_USER;Password=DB_PASSWORD"
  },
  "Security": {
    "AllowedOrigin": "https://rdp.example.com",
    "KeyRingPath": "App_Data/keys",
    "KeyRingCertificate": "App_Data/keyring.pfx",
    "KeyRingPassword": "PFX_PASSWORD"
  }
}
```

| Engine | Provider | Connection string | ServerVersion |
|---|---|---|---|
| PostgreSQL | `PostgreSQL` | `Host=DB_HOST;Port=5432;Database=DB_NAME;Username=DB_USER;Password=DB_PASSWORD` | Omit |
| MySQL | `MySQL` | `Server=DB_HOST;Port=3306;Database=DB_NAME;User ID=DB_USER;Password=DB_PASSWORD` | Actual numeric version, e.g. `8.4.0` |
| MariaDB | `MariaDB` | Same syntax as MySQL | Actual numeric version, e.g. `11.4.5` |

For remote databases, follow the provider's CA/certificate instructions: PostgreSQL `SSL Mode=VerifyFull`, MySQL/MariaDB `SslMode=VerifyFull`. Local and remote database TLS requirements may differ. Changing the provider setting does not migrate existing data between engines.

`App_Data/keys` must persist across deployments and be writable only by the required API identity. The PFX protects Data Protection keys; it is separate from the website's HTTPS certificate. Preserve existing keys and PFX during upgrades.

For a fresh installation, open PowerShell on a Windows administration computer **in a protected directory outside all source repositories**:

```powershell
New-Item -ItemType Directory -Force .\private-remvora-keys | Out-Null
$cert = New-SelfSignedCertificate -Subject 'CN=Remvora Data Protection' -CertStoreLocation 'Cert:\CurrentUser\My' -KeyAlgorithm RSA -KeyLength 2048 -KeyExportPolicy Exportable -NotAfter (Get-Date).AddYears(5)
$pfxPassword = Read-Host 'PFX password' -AsSecureString
Export-PfxCertificate -Cert $cert -FilePath .\private-remvora-keys\keyring.pfx -Password $pfxPassword | Out-Null
Remove-Variable pfxPassword
```

Transfer the PFX privately to the API's `App_Data/keyring.pfx`, set the same `Security:KeyRingPassword`, and protect its private local copy. Do not commit it. The [general guide](GUIDE.en.md#practical-secret-entry-and-key-ring-certificate-example) includes an OpenSSL alternative.

## 5. Create tables using migration

**On the deployed API machine**, open PowerShell in the actual API physical directory supplied by Plesk. Replace this example path:

```powershell
Set-Location 'D:\YOUR_API_PUBLISH_DIRECTORY'
$env:ASPNETCORE_ENVIRONMENT = 'Production'
& '.\Remvora.Api.exe' --migrate
if ($LASTEXITCODE -ne 0) { throw 'Migration failed; do not continue.' }
```

This creates/updates the chosen engine's tables, indexes and `__EFMigrationsHistory`. It does **not** create the first user. Run migration and bootstrap as two separate invocations, never as combined flags.

Without console access, ask hosting support to run `Remvora.Api.exe --migrate` in your application directory with the Production environment and configuration. Check the exit code/log; do not put passwords in public task descriptions.

## 6. Create the first Owner

In the same publish directory, with the same database and Production configuration:

```powershell
$env:ASPNETCORE_ENVIRONMENT = 'Production'
$env:REMVORA_ADMIN_EMAIL = Read-Host 'First administrator email'
$secret = Read-Host 'Administrator password (8-256 characters)' -AsSecureString
$env:REMVORA_ADMIN_PASSWORD = [System.Net.NetworkCredential]::new('', $secret).Password
$env:REMVORA_ORGANIZATION = Read-Host 'Organization name'
try {
    & '.\Remvora.Api.exe' --bootstrap
    if ($LASTEXITCODE -ne 0) { throw 'Bootstrap failed; inspect the error.' }
} finally {
    Remove-Item Env:\REMVORA_ADMIN_EMAIL, Env:\REMVORA_ADMIN_PASSWORD, Env:\REMVORA_ORGANIZATION -ErrorAction SilentlyContinue
    Remove-Variable secret -ErrorAction SilentlyContinue
}
```

Success prints `Organization: <UUID>`. Bootstrap writes the organization and its **Owner** account to the database with a hashed password. Sign in using this email/password, not hosting/database/SSH credentials. You do not type the organization UUID into the login form. There is no default administrator password or automatic invitation email.

`Bootstrap only supports an empty installation` means initialization already occurred. Do not delete tables; bootstrap is neither a second-user nor a password-reset command.

An alternative for a hosting provider without interactive console access is [initialize.ps1](../deploy/windows/initialize.ps1). For a fresh installation only, keep `app_offline.htm`, prepare production configuration and a protected **HTTP-inaccessible** `App_Data/bootstrap.json`:

```json
{"email":"owner@example.com","password":"REPLACE_WITH_UNIQUE_OWNER_PASSWORD","organization":"My organization"}
```

The provider runs `powershell -File initialize.ps1 -ApplicationPath "D:\YOUR_API_PUBLISH_DIRECTORY"`. The script runs migration and bootstrap, deletes the bootstrap file only on success and keeps maintenance enabled. On failure protect the file and remove it after resolving the problem. Never put this password in a repository, web-panel directory or public support ticket. Prefer private interactive/environment input when available.

## 7. Open the application and sign in

1. After both commands succeed, remove only this API's `app_offline.htm`. IIS must use the same Production settings.
2. Check `https://api.example.com/health` and `/health/ready`. Readiness checks database connectivity; it is not a remote-session test.
3. Open `https://rdp.example.com` and sign in with the account created in step 6.
4. Review Profile/Organization. Optionally enable TOTP under Security and store recovery codes privately.
5. Remove public deployment archives and temporary setup secrets. Back up the database, key ring/PFX and private configuration together.

## 8. Add subsequent users

Sign in as Owner → **Users** → add email, initial password and role → save. Restrict device-group scope where needed. Deliver initial credentials privately; no invitation email is automatically sent. Users can change their password from their profile using their current password. Owners manage accounts and permissions inside their organization but cannot read existing passwords. This release has no additional-organization creation wizard.

## 9. Connect your first device

Create a device/enrollment token in the panel. Follow the [Agent guide](https://github.com/tahayildirm/remvora-agent/blob/main/docs/GUIDE.en.md): `enroll` → approve in the panel → `activate` → start with explicit capabilities. The agent connects to `https://api.example.com/`, not the panel origin. Open Desktop or Terminal; verify actual online presence, the P2P/WSS indicator and a real input/command response.

## Common first-install problems

| Symptom | Check |
|---|---|
| Database access denied | Full database/user names, host/port, source-host grants and required TLS settings |
| Missing tables | Successful migration against the database the running API actually uses |
| Login rejected | Bootstrap success and correct database; migration does not create accounts |
| Production key-ring error | PFX path/password, permissions and persistent keys directory |
| Panel cannot reach server | Running API, HTTPS, panel API-origin metadata and exact `Security:AllowedOrigin` |
| Offline device | Agent API origin, enrollment/identity, service and WSS connection |
| Slower across networks | P2P/WSS indicator, both access links and relay hosting throughput |

[Administration guide](GUIDE.en.md) · [Current limitations](STATUS.md) · [Security](../SECURITY.md)
