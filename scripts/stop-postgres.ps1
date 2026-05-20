# Stop the local portable PostgreSQL.

$ErrorActionPreference = 'Continue'
$root = Split-Path -Parent $PSScriptRoot
$bin  = Join-Path $root '.pg\pgsql\bin'
$data = Join-Path $root '.pg\data'

if (-not (Test-Path "$bin\pg_ctl.exe")) {
    Write-Host "Postgres binaries not found, nothing to stop."
    exit 0
}

& "$bin\pg_isready.exe" -h 127.0.0.1 -p 5432 -U postgres 2>$null
if ($LASTEXITCODE -ne 0) {
    Write-Host "Postgres already stopped." -ForegroundColor Yellow
    exit 0
}

& "$bin\pg_ctl.exe" -D $data -m fast stop
