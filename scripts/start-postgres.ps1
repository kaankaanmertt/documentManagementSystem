# Start the local portable PostgreSQL (if not already running).

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$bin  = Join-Path $root '.pg\pgsql\bin'
$data = Join-Path $root '.pg\data'
$log  = Join-Path $root '.pg\postgres.log'

if (-not (Test-Path "$bin\pg_ctl.exe")) {
    Write-Error "Postgres binaries not found. Run scripts\setup-postgres.ps1 first."
    exit 1
}

& "$bin\pg_isready.exe" -h 127.0.0.1 -p 5432 -U postgres 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Postgres already running." -ForegroundColor Green
    exit 0
}

& "$bin\pg_ctl.exe" -D $data -l $log -w start
& "$bin\pg_isready.exe" -h 127.0.0.1 -p 5432 -U postgres
