# Document Management System - one-shot PostgreSQL setup script
#
# Steps:
#   1. Download portable PostgreSQL 16 binaries (~290 MB) - only on first run
#   2. Extract to .pg/
#   3. Initialize data directory (initdb)
#   4. Start postgres in the background
#   5. Create dmsuser + documentdb
#   6. Apply init.sql (schema + indexes)
#
# No admin required. Does not install a Windows service.
# Does not interfere with any other PostgreSQL installed on the machine.
# Stop later with: scripts\stop-postgres.ps1

# PowerShell 5.1, native exe'lerin stderr satirlarini ErrorRecord olarak sariyor;
# bu yuzden 'Stop' kullanamiyoruz, manuel olarak $LASTEXITCODE kontrol ediyoruz.
$ErrorActionPreference = 'Continue'

function Assert-LastExit {
    param([string]$ctx)
    if ($LASTEXITCODE -ne 0) {
        Write-Host ""
        Write-Host "[ FAIL ] $ctx (exit $LASTEXITCODE)" -ForegroundColor Red
        exit $LASTEXITCODE
    }
}
$root = Split-Path -Parent $PSScriptRoot
$pgDir   = Join-Path $root '.pg'
$pgsql   = Join-Path $pgDir 'pgsql'
$bin     = Join-Path $pgsql 'bin'
$data    = Join-Path $pgDir 'data'
$cache   = Join-Path $root '.cache'
$zip     = Join-Path $cache 'pg16.zip'
$logfile = Join-Path $pgDir 'postgres.log'
$pwfile  = Join-Path $pgDir 'pwfile.txt'
$initSql = Join-Path $root 'docker\postgres\init.sql'

$pgUrl = 'https://get.enterprisedb.com/postgresql/postgresql-16.6-1-windows-x64-binaries.zip'

function Section($msg) { Write-Host "`n=== $msg ===" -ForegroundColor Cyan }

Section '1/6  PostgreSQL binaries'
if (-not (Test-Path "$bin\postgres.exe")) {
    if (-not (Test-Path $cache))  { New-Item -ItemType Directory -Path $cache  | Out-Null }
    if (-not (Test-Path $pgDir))  { New-Item -ItemType Directory -Path $pgDir  | Out-Null }

    if (-not (Test-Path $zip)) {
        Write-Host "Downloading Postgres 16.6 binaries (~290 MB), this may take a few minutes..."
        $sw = [Diagnostics.Stopwatch]::StartNew()
        (New-Object System.Net.WebClient).DownloadFile($pgUrl, $zip)
        $sw.Stop()
        Write-Host ("Download finished: {0:N1} sec" -f $sw.Elapsed.TotalSeconds)
    } else {
        Write-Host "Zip already present: $zip"
    }

    Write-Host "Extracting..."
    $sw = [Diagnostics.Stopwatch]::StartNew()
    Expand-Archive -Path $zip -DestinationPath $pgDir -Force
    $sw.Stop()
    Write-Host ("Extract finished: {0:N1} sec" -f $sw.Elapsed.TotalSeconds)
} else {
    Write-Host "Binaries already present: $bin"
}

Section '2/6  Data directory'
if (-not (Test-Path $data)) {
    Set-Content -Path $pwfile -Value 'dmspass' -Encoding ascii -NoNewline
    & "$bin\initdb.exe" -D $data -U postgres --pwfile=$pwfile -E UTF8 --locale=C 2>&1 | Select-Object -Last 6
    Remove-Item $pwfile -ErrorAction SilentlyContinue
    Write-Host "initdb done."
} else {
    Write-Host "Data dir already exists: $data"
}

# 1M-row dataset icin tuning. Idempotent: zaten varsa eklemez.
$confPath = Join-Path $data 'postgresql.conf'
$confText = Get-Content $confPath -Raw -ErrorAction SilentlyContinue
if ($confText -and $confText -notlike '*Document Management System tuning*') {
    $tuning = @"

# === Document Management System tuning (1M rows, dev machine) ===
# Production: tune to actual server RAM.
shared_buffers = 512MB
effective_cache_size = 2GB
work_mem = 32MB
maintenance_work_mem = 128MB
random_page_cost = 1.1
"@
    Add-Content -Path $confPath -Value $tuning -Encoding ascii
    Write-Host "postgresql.conf tuned for 1M rows."
}

Section '3/6  Start postgres'
& "$bin\pg_isready.exe" -h 127.0.0.1 -p 5432 -U postgres 2>$null
if ($LASTEXITCODE -eq 0) {
    Write-Host "Postgres already running (port 5432)."
} else {
    & "$bin\pg_ctl.exe" -D $data -l $logfile -w start 2>&1 | Select-Object -Last 4
    Start-Sleep -Seconds 2
    & "$bin\pg_isready.exe" -h 127.0.0.1 -p 5432 -U postgres
}

Section '4/6  Database user'
$env:PGPASSWORD = 'dmspass'
$userExists = (& "$bin\psql.exe" -h 127.0.0.1 -U postgres -d postgres -tAc "SELECT 1 FROM pg_roles WHERE rolname='dmsuser'").Trim()
if ($userExists -ne '1') {
    Write-Output "CREATE USER dmsuser WITH PASSWORD 'dmspass' SUPERUSER;" | & "$bin\psql.exe" -h 127.0.0.1 -U postgres -d postgres
} else {
    Write-Host "dmsuser already exists."
}

Section '5/6  Database'
$dbExists = (& "$bin\psql.exe" -h 127.0.0.1 -U postgres -d postgres -tAc "SELECT 1 FROM pg_database WHERE datname='documentdb'").Trim()
if ($dbExists -ne '1') {
    Write-Output "CREATE DATABASE documentdb OWNER dmsuser;" | & "$bin\psql.exe" -h 127.0.0.1 -U postgres -d postgres
} else {
    Write-Host "documentdb already exists."
}

Section '6/6  Schema (init.sql)'
& "$bin\psql.exe" -h 127.0.0.1 -U dmsuser -d documentdb -v ON_ERROR_STOP=1 -f $initSql 2>&1 | Select-Object -Last 6
Assert-LastExit 'psql init.sql'

Write-Host ""
Write-Host "[ OK ] PostgreSQL is ready." -ForegroundColor Green
Write-Host "  Host:     127.0.0.1:5432"
Write-Host "  Database: documentdb"
Write-Host "  User:     dmsuser / Password: dmspass"
Write-Host ""
Write-Host "Next step:"
Write-Host "  cd src\DocumentManagementSystem"
Write-Host "  dotnet run"
Write-Host ""
Write-Host "First start seeds 1,000,000 sample documents (~75 seconds)."
