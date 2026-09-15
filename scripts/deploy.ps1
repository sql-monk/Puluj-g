<#
.SYNOPSIS  Rebuilds and restarts the Docker stack (deploy/docker-compose.yml, project "puluj-g") from the working tree,
           waits for the one-shot migrate service, runs the one-off SQL fixes and then checks that the stack is healthy.
           Everything here needs Docker on the host, which the Claude Code session is not allowed to drive — run it
           yourself from the repo root:  .\scripts\deploy.ps1
           The steps are idempotent: a second run only rebuilds what changed, the SQL scripts touch nothing when
           there is nothing left to fix.
.PARAMETER NoBuild   Restart with the existing images (no `--build`).
.PARAMETER SkipSql   Skip the one-off SQL scripts (scripts/requeue-failed.sql, scripts/fix-text-alert-ends.sql).
.PARAMETER Services  Rebuild only these compose services (e.g. api,admin); default — the whole stack.
#>
param([switch]$NoBuild, [switch]$SkipSql, [string[]]$Services = @())
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$deploy = Join-Path $root "deploy"
$composeProject = "puluj-g"
$dockerBin = "C:\Program Files\Docker\Docker\resources\bin"
if (-not (Get-Command docker -ErrorAction SilentlyContinue) -and (Test-Path "$dockerBin\docker.exe")) { $env:PATH = "$env:PATH;$dockerBin" }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "docker not found (Docker Desktop is not installed or not in PATH)" }

function Step([string]$title) { Write-Host "`n=== $title ===" -ForegroundColor Cyan }
function Sql([string]$file) {
    # psql is not installed on the host: the script goes through the postgis container (Cyrillic-safe via stdin).
    Get-Content -Raw -Encoding UTF8 $file | docker exec -i $postgisContainer psql -U puluj -d puluj -v ON_ERROR_STOP=1 -f -
    if ($LASTEXITCODE -ne 0) { throw "psql failed for $file" }
}

function ComposeContainerId([string]$service) {
    $ids = @(& docker compose -p $composeProject ps -aq $service | Where-Object { $_ })
    if ($ids.Count -ne 1) { throw "Expected exactly one $service container in compose project '$composeProject', found $($ids.Count)." }
    return $ids[0].Trim()
}

# A local dev-run Worker next to the Docker processors means two processor versions over one database (the deadlocks
# of 15.09) and two Telegram clients on one session: stop it first.
$local = Get-Process -Name "Puluj.Worker", "Puluj.Api", "Puluj.Admin", "Puluj.Analytics.Worker" -ErrorAction SilentlyContinue
if ($local) {
    Step "Stopping local dev-run processes ($($local.Name -join ', '))"
    $local | Stop-Process -Force
}

Step "Building and starting the stack"
Push-Location $deploy
try {
    if (-not (Test-Path ".env")) { Write-Warning "deploy/.env is missing: compose will use the defaults from docker-compose.yml (ADMIN_TOKEN empty = panel only from localhost)" }
    $composeArgs = @("compose", "-p", $composeProject, "up", "-d", "--remove-orphans")
    if (-not $NoBuild) { $composeArgs += "--build" }
    $composeArgs += $Services
    & docker @composeArgs
    if ($LASTEXITCODE -ne 0) { throw "docker compose up failed" }

    Step "Waiting for the migrate service (migrations + seed)"
    $deadline = (Get-Date).AddMinutes(30)
    do {
        $migrateContainer = ComposeContainerId "migrate"
        $state = docker inspect --format '{{.State.Status}} {{.State.ExitCode}}' $migrateContainer 2>$null
        if ($state -like "exited 0*") { break }
        if ($state -like "exited *") { docker logs --tail 50 $migrateContainer; throw "migrate exited with $state" }
        Start-Sleep 5
    } while ((Get-Date) -lt $deadline)
    Write-Host "migrate: $state"
    docker logs $migrateContainer 2>&1 | Select-String -Pattern "Applying|migration|Seeding" | Select-Object -Last 8

    Step "Containers"
    docker compose -p $composeProject ps --format "table {{.Name}}\t{{.Service}}\t{{.Status}}\t{{.Image}}"
    $postgisContainer = ComposeContainerId "postgis"
    $processorContainers = @(& docker compose -p $composeProject ps -q processor | Where-Object { $_ })
} finally { Pop-Location }

if (-not $SkipSql) {
    Step "One-off SQL: Failed raw messages back to Pending (deadlock victims of 15.09)"
    Sql (Join-Path $root "scripts\requeue-failed.sql")
    Step "One-off SQL: text alerts closed by an out-of-order 'відбій' (ended_at < started_at) reopened for the watchdog"
    Sql (Join-Path $root "scripts\fix-text-alert-ends.sql")
}

Step "Checks"
Start-Sleep 20  # let the processors claim a few messages so the new log lines exist
$since = (Get-Date).AddMinutes(-2).ToUniversalTime().ToString("o")
$deadlocks = (docker logs --since $since $postgisContainer 2>&1 | Select-String "deadlock detected").Count
Write-Host ("PostgreSQL deadlocks since restart: {0}" -f $deadlocks) -ForegroundColor ($(if ($deadlocks -eq 0) { "Green" } else { "Red" }))
foreach ($c in $processorContainers) {
    $line = docker logs --tail 200 $c 2>&1 | Select-String "lock wait" | Select-Object -Last 1
    Write-Host ("{0}: {1}" -f $c, $(if ($line) { "new timing format OK (parse / lock wait / store)" } else { "no 'lock wait' line yet (idle, or old image?)" }))
    $err = (docker logs --tail 500 $c 2>&1 | Select-String '"@l":"Error"').Count
    if ($err -gt 0) { Write-Warning "$c has $err error line(s) in the last 500 — see docker logs $c" }
}
foreach ($u in @("http://localhost:8090/api/health", "http://localhost:8091/api/health")) {
    try { $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $u; Write-Host ("{0} -> {1}" -f $u, $r.StatusCode) }
    catch { Write-Warning "$u -> $($_.Exception.Message)" }
}
Step "Queue"
@"
SELECT processing_status, count(*) FROM raw_messages GROUP BY 1 ORDER BY 1;
SELECT count(*) AS text_alerts_ended_before_start FROM air_alerts WHERE ended_at < started_at;
SELECT key, left(value, 60) AS value FROM app_settings WHERE key LIKE 'Runtime:Worker:%' ORDER BY 1;
"@ | docker exec -i $postgisContainer psql -U puluj -d puluj -f -
Write-Host "`nDone. Map: http://localhost:8090  Admin: http://localhost:8091" -ForegroundColor Green
