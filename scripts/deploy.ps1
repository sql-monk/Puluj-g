<#
.SYNOPSIS  Rebuilds and restarts the Docker stack (deploy/docker-compose.yml, project "puluj-g") from the working tree,
           waits for the one-shot migrate service, runs the one-off SQL fixes and then checks that the stack is healthy.
           Everything here needs Docker on the host, which the Claude Code session is not allowed to drive — run it
           yourself from the repo root:  .\scripts\deploy.ps1
           The steps are idempotent: a second run rebuilds what changed and applies outstanding migrations to the
           existing database; it never replaces its data or settings.
.PARAMETER NoBuild   Restart with the existing images (no `--build`).
.PARAMETER SkipSql   Skip the one-off SQL scripts (scripts/requeue-failed.sql, scripts/fix-text-alert-ends.sql).
.PARAMETER Services  Rebuild only these compose services (e.g. api,admin); default — the whole stack.
.PARAMETER DatabaseVolume
           Existing Docker volume that contains PostgreSQL data. Defaults to puluj-g-pgdata.
.PARAMETER InitializeDatabase
           Create DatabaseVolume when it does not exist. Required only for a deliberately new, empty installation.
.PARAMETER Broker
           Start the `broker` profile (RabbitMQ + the `messaging` worker: relay, archive, raw-writer, normalizer, parser,
           llm-worker, finalizer) and route the collectors through the single ingress (MESSAGING_OUTBOX_ENABLED /
           MESSAGING_INGRESS_ENABLED = true). Without it the platform path is off and the legacy processor writes the domain.
.PARAMETER DomainWriters
           P09/P10 cutover (ADR-0009/0010): the legacy `processor` role is stopped and scaled to 0 BEFORE the `messaging` worker
           gets the track-worker, alert-worker, watchdog and incident-worker roles — never two owners of tracks/alerts over one database.
           Requires -Broker. Rollback: run again without -DomainWriters (processor back to 2 replicas, writers roles off;
           the guards in both directions keep the rows consistent).
#>
param(
    [switch]$NoBuild,
    [switch]$SkipSql,
    [string[]]$Services = @(),
    [string]$DatabaseVolume = "puluj-g-pgdata",
    [switch]$InitializeDatabase,
    [switch]$Broker,
    [switch]$DomainWriters
)
if ($DomainWriters -and -not $Broker) { throw "-DomainWriters needs -Broker: the writers consume observations.recorded from RabbitMQ (ADR-0009)" }
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$deploy = Join-Path $root "deploy"
$composeProject = "puluj-g"
$dockerBin = "C:\Program Files\Docker\Docker\resources\bin"
if (-not (Get-Command docker -ErrorAction SilentlyContinue) -and (Test-Path "$dockerBin\docker.exe")) { $env:PATH = "$env:PATH;$dockerBin" }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "docker not found (Docker Desktop is not installed or not in PATH)" }
if ([string]::IsNullOrWhiteSpace($DatabaseVolume)) { throw "DatabaseVolume must not be empty" }

# The Postgres volume is external: Compose must never silently make a fresh database when a volume name was mistyped
# or the persistent disk was not mounted.  A first install is deliberately opt-in via -InitializeDatabase.
$volume = $DatabaseVolume.Trim()
$volumeExists = (& docker volume inspect $volume 2>$null) -and $LASTEXITCODE -eq 0
if (-not $volumeExists) {
    if (-not $InitializeDatabase) {
        throw "PostgreSQL volume '$volume' does not exist. Refusing to create a new database; restore or pass -DatabaseVolume <existing-volume>. For a new installation, run again with -InitializeDatabase."
    }
    Step "Creating an empty PostgreSQL volume '$volume'"
    & docker volume create $volume | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not create PostgreSQL volume '$volume'" }
}
else {
    Write-Host "Using existing PostgreSQL volume '$volume'; migrations will update it in place." -ForegroundColor Green
}
$env:PULUJ_PGDATA_VOLUME = $volume

function Step([string]$title) { Write-Host "`n=== $title ===" -ForegroundColor Cyan }

# Platform path (P03–P09): which roles the `messaging` worker runs and how many legacy processors stay. Compose reads
# these through ${…} substitution, so they are set here per run — the plain run always restores the legacy layout.
$defaultMessagingRoles = "relay,archive,raw-writer,normalizer,parser,llm-worker,finalizer,projection,replay"
$profileArgs = @()
if ($Broker) {
    $profileArgs = @("--profile", "broker")
    $env:MESSAGING_OUTBOX_ENABLED = "true"
    $env:MESSAGING_INGRESS_ENABLED = "true"
}
if ($DomainWriters) {
    $env:MESSAGING_WORKER_ROLES = "$defaultMessagingRoles,track-worker,alert-worker,watchdog,incident-worker"
    $env:PROCESSOR_REPLICAS = "0"
} else {
    $env:MESSAGING_WORKER_ROLES = $defaultMessagingRoles
    $env:PROCESSOR_REPLICAS = "2"
}
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

if ($DomainWriters) {
    # ADR-0009 cutover order: the legacy owner stops first, the writers start after it. The processors' in-flight
    # transactions finish on SIGTERM; the shared Store lock means a writer can never interleave with a live legacy write.
    Step "Cutover (ADR-0009): stopping the legacy processor role before the domain writers start"
    Push-Location $deploy
    try {
        & docker compose -p $composeProject @profileArgs stop processor
        if ($LASTEXITCODE -ne 0) { throw "could not stop the processor service" }
    } finally { Pop-Location }
}

Step ("Building and starting the stack" + $(if ($Broker) { " (broker profile" + $(if ($DomainWriters) { ", domain writers" }) + ")" } else { "" }))
Push-Location $deploy
try {
    if (-not (Test-Path ".env")) { Write-Warning "deploy/.env is missing: compose will use the defaults from docker-compose.yml (ADMIN_TOKEN empty = panel only from localhost)" }
    $composeArgs = @("compose", "-p", $composeProject) + $profileArgs + @("up", "-d", "--remove-orphans")
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
    docker compose -p $composeProject @profileArgs ps --format "table {{.Name}}\t{{.Service}}\t{{.Status}}\t{{.Image}}"
    $postgisContainer = ComposeContainerId "postgis"
    $processorContainers = @(& docker compose -p $composeProject ps -q processor | Where-Object { $_ })
    if ($DomainWriters -and $processorContainers.Count -gt 0) { throw "processor containers are still running after the cutover: $($processorContainers -join ', ')" }
    $messagingContainers = @(& docker compose -p $composeProject @profileArgs ps -q messaging | Where-Object { $_ })
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
foreach ($c in $messagingContainers) {
    $roles = docker exec $c printenv Worker__Roles 2>$null
    Write-Host ("{0}: roles {1}" -f $c, $roles)
    if ($DomainWriters -and $roles -notmatch "track-worker") { Write-Warning "$c does not run the domain writers (Worker__Roles=$roles)" }
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
SELECT count(*) FILTER (WHERE observation_id IS NOT NULL) AS targets_by_writers, count(*) FILTER (WHERE observation_id IS NULL) AS targets_by_legacy FROM targets;
"@ | docker exec -i $postgisContainer psql -U puluj -d puluj -f -
if ($Broker) {
@"
SELECT subscription_id, outcome, count(*) FROM processing.deliveries GROUP BY 1, 2 ORDER BY 1, 2;
SELECT count(*) AS outbox_unconfirmed FROM messaging.outbox WHERE confirmed_at IS NULL;
"@ | docker exec -i $postgisContainer psql -U puluj -d puluj -f -
}
Write-Host "`nDone. Map: http://localhost:8090  Admin: http://localhost:8091" -ForegroundColor Green
