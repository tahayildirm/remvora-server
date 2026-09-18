# Run from a server console after secure upload, while app_offline.htm remains present.
# This script deliberately does not activate the public application.
[CmdletBinding()]
param([Parameter(Mandatory=$true)][string]$ApplicationPath)
$ErrorActionPreference = 'Stop'
$root = (Resolve-Path -LiteralPath $ApplicationPath).Path
if (!(Test-Path -LiteralPath (Join-Path $root 'Remvora.Api.exe'))) { throw 'Remvora API not found' }
if (!(Test-Path -LiteralPath (Join-Path $root 'app_offline.htm'))) { throw 'Put this application into maintenance first' }
$settingsPath = Join-Path $root 'appsettings.Production.json'
$settings = Get-Content -LiteralPath $settingsPath -Raw | ConvertFrom-Json
if ($settings.Database.Provider -notin @('PostgreSQL','MySQL','MariaDB') -or !$settings.ConnectionStrings.Database -or ($settings.Database.Provider -ne 'PostgreSQL' -and !$settings.Database.ServerVersion)) {
    throw 'Supported database provider, connection and (for MySQL/MariaDB) server version are required'
}
$bootstrapPath = Join-Path $root 'App_Data/bootstrap.json'
$bootstrap = Get-Content -LiteralPath $bootstrapPath -Raw | ConvertFrom-Json
if (!$bootstrap.email -or $bootstrap.password.Length -lt 8 -or $bootstrap.password.Length -gt 256) { throw 'Valid administrator input required' }
$names = @('ASPNETCORE_ENVIRONMENT','REMVORA_ADMIN_EMAIL','REMVORA_ADMIN_PASSWORD','REMVORA_ORGANIZATION')
$previous = @{}
foreach ($name in $names) { $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process') }
Push-Location $root
try {
    $env:ASPNETCORE_ENVIRONMENT = 'Production'
    & '.\Remvora.Api.exe' --migrate
    if ($LASTEXITCODE -ne 0) { throw 'Database migration failed; maintenance retained' }
    $env:REMVORA_ADMIN_EMAIL = $bootstrap.email
    $env:REMVORA_ADMIN_PASSWORD = $bootstrap.password
    $env:REMVORA_ORGANIZATION = $bootstrap.organization
    & '.\Remvora.Api.exe' --bootstrap
    if ($LASTEXITCODE -ne 0) { throw 'Administrator bootstrap failed; do not rerun against an initialized database' }
    Remove-Item -LiteralPath $bootstrapPath
    Write-Output 'Initialization succeeded. Record the organization ID above. Maintenance remains enabled pending activation checks.'
}
finally {
    foreach ($name in $names) { [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process') }
    $bootstrap = $null
    Pop-Location
}
