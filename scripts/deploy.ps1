<#
.SYNOPSIS  Interactive deployment wizard and non-interactive Docker deployment command for the Puluj-G Compose stack.
           With no parameters it lets an operator choose services, image rebuild, database handling, optional broker
           mode, and runtime credentials. Existing command-line parameters remain available for CI and runbooks.
           The steps are idempotent: a second run rebuilds what changed and applies outstanding migrations to the
           existing database; it never replaces its data or settings.
.PARAMETER NoBuild   Restart with the existing images (no `--build`).
.PARAMETER SkipSql   Skip the one-off SQL scripts (scripts/requeue-failed.sql, scripts/fix-text-alert-ends.sql).
.PARAMETER Services  Rebuild only these compose services (e.g. api,admin); default — the whole stack.
.PARAMETER DatabaseVolume
           Existing Docker volume that contains PostgreSQL data. Defaults to puluj-g-pgdata.
.PARAMETER InitializeDatabase
           Create DatabaseVolume when it does not exist. Required only for a deliberately new, empty installation.
.PARAMETER ResetDatabase
           Destroy the whole isolated Puluj-G deployment state, recreate its PostgreSQL volume, and then run the
           normal migration/seed/start sequence. This is destructive and requires -ConfirmReset. It also removes this
           Compose project's non-external volumes (Telegram session, logs and RabbitMQ state); configure collector
           secrets again afterwards. It never resets a volume outside the exact '<ComposeProject>-pgdata' target.
.PARAMETER ConfirmReset
           Explicit acknowledgement required together with -ResetDatabase. Without it the script stops before touching
           Docker resources.
.PARAMETER ComposeProject
           Isolated Compose project name. Defaults to puluj-g. Only names beginning with puluj-g are accepted, and a
           reset accepts only its matching '<ComposeProject>-pgdata' database volume.
.PARAMETER Broker
           Start the `broker` profile (RabbitMQ + the `messaging` worker: relay, archive, raw-writer, normalizer, parser,
           llm-worker, finalizer) and route the collectors through the single ingress (MESSAGING_OUTBOX_ENABLED /
           MESSAGING_INGRESS_ENABLED = true). Without it the platform path is off and the legacy processor writes the domain.
.PARAMETER DomainWriters
           P09/P10 cutover (ADR-0009/0010): the legacy `processor` role is stopped and scaled to 0 BEFORE the `messaging` worker
           gets the track-worker, alert-worker, watchdog and incident-worker roles — never two owners of tracks/alerts over one database.
           Requires -Broker. Rollback: run again without -DomainWriters (processor back to 2 replicas, writers roles off;
           the guards in both directions keep the rows consistent).
.PARAMETER ProcessorReplicas
           Number of legacy `processor` replicas (0–32). Defaults to 2. Domain writers always set this to 0 because
           the messaging worker is then the only owner of tracks, alerts and incidents.
.PARAMETER Wizard
           Force the interactive wizard even when other command-line parameters were supplied.
.PARAMETER NonInteractive
           Do not prompt. Intended for CI/runbooks; use the remaining parameters exactly as before.
#>
param(
    [switch]$NoBuild,
    [switch]$SkipSql,
    [string[]]$Services = @(),
    [string]$DatabaseVolume = "puluj-g-pgdata",
    [switch]$InitializeDatabase,
    [switch]$ResetDatabase,
    [switch]$ConfirmReset,
    [string]$ComposeProject = "puluj-g",
    [switch]$Broker,
    [switch]$DomainWriters,
    [ValidateRange(0, 32)]
    [int]$ProcessorReplicas = 2,
    [switch]$Wizard,
    [switch]$NonInteractive
)
$ErrorActionPreference = "Stop"
$root = Resolve-Path "$PSScriptRoot\.."
$deploy = Join-Path $root "deploy"
$composeProject = $ComposeProject
$envFile = Join-Path $deploy ".env"
$wizardSettings = [ordered]@{}
$applyWizardSettingsToDatabase = $false
$selectedServicesOnly = $false

function Step([string]$title) { Write-Host "`n=== $title ===" -ForegroundColor Cyan }

function Read-Choice([string]$Prompt, [string[]]$Allowed) {
    do { $answer = (Read-Host $Prompt).Trim() } while ($answer -notin $Allowed)
    return $answer
}

function Read-YesNo([string]$Prompt, [bool]$Default = $true) {
    $suffix = if ($Default) { "[Y/n]" } else { "[y/N]" }
    $answer = (Read-Host "$Prompt $suffix").Trim()
    if ([string]::IsNullOrEmpty($answer)) { return $Default }
    if ($answer -match '^(y|yes|т|так)$') { return $true }
    if ($answer -match '^(n|no|н|ні)$') { return $false }
    Write-Warning "Введіть Y або N."
    return Read-YesNo $Prompt $Default
}

function Read-ProcessorReplicaCount([int]$Default) {
    do {
        $answer = (Read-Host "Кількість processor-реплік 0–32 (Enter — $Default)").Trim()
        if ([string]::IsNullOrEmpty($answer)) { return $Default }
        $parsed = 0
        if ([int]::TryParse($answer, [ref]$parsed) -and $parsed -ge 0 -and $parsed -le 32) { return $parsed }
        Write-Warning "Введіть ціле число від 0 до 32. 0 зупиняє legacy processor."
    } while ($true)
}

function Get-DotEnvValues {
    $values = @{}
    if (-not (Test-Path -LiteralPath $envFile)) { return $values }
    foreach ($line in Get-Content -LiteralPath $envFile) {
        if ($line -match '^\s*([A-Za-z_][A-Za-z0-9_]*)\s*=\s*(.*)$') {
            $value = $Matches[2].Trim()
            if ($value.Length -ge 2 -and (($value.StartsWith("'") -and $value.EndsWith("'")) -or ($value.StartsWith('"') -and $value.EndsWith('"')))) {
                $value = $value.Substring(1, $value.Length - 2)
            }
            $values[$Matches[1]] = $value
        }
    }
    return $values
}

function Read-Setting([hashtable]$Current, [string]$Key, [string]$Label, [bool]$Secret = $false) {
    $state = if ($Current.ContainsKey($Key) -and -not [string]::IsNullOrWhiteSpace($Current[$Key])) { if ($Secret) { "задано" } else { "поточне: $($Current[$Key])" } } else { "не задано" }
    $value = if ($Secret) {
        $secure = Read-Host "$Label ($state; Enter — не змінювати, '-' — очистити)" -AsSecureString
        $ptr = [Runtime.InteropServices.Marshal]::SecureStringToBSTR($secure)
        try { [Runtime.InteropServices.Marshal]::PtrToStringBSTR($ptr) } finally { [Runtime.InteropServices.Marshal]::ZeroFreeBSTR($ptr) }
    } else {
        Read-Host "$Label ($state; Enter — не змінювати, '-' — очистити)"
    }
    if ([string]::IsNullOrEmpty($value)) { return }
    $wizardSettings[$Key] = if ($value -eq '-') { '' } else { $value.Trim() }
}

function Update-DotEnv([System.Collections.IDictionary]$Values) {
    $lines = if (Test-Path -LiteralPath $envFile) { [System.Collections.Generic.List[string]]@(Get-Content -LiteralPath $envFile) } else { [System.Collections.Generic.List[string]]::new() }
    foreach ($entry in $Values.GetEnumerator()) {
        $key = $entry.Key
        # Single quotes keep $, # and whitespace literal for Docker Compose. A literal quote is escaped in dotenv syntax.
        $escaped = ([string]$entry.Value).Replace("'", "\'")
        $replacement = "$key='$escaped'"
        $index = -1
        for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i] -match "^\s*$([regex]::Escape($key))\s*=") { $index = $i; break } }
        if ($index -ge 0) { $lines[$index] = $replacement } else { $lines.Add($replacement) }
    }
    Set-Content -LiteralPath $envFile -Value $lines -Encoding utf8NoBOM
    Write-Host "Оновлено deploy/.env; значення секретів не показані." -ForegroundColor Green
}

function Select-Services {
    $services = [ordered]@{
        'postgis' = 'PostgreSQL/PostGIS: постійні дані, геометрія та черги'
        'migrate' = 'одноразово застосовує EF-міграції й seed-дані'
        'collector-telegram' = 'зчитує повідомлення з Telegram-каналів'
        'collector-alerts' = 'отримує повітряні тривоги з alerts.in.ua'
        'processor' = 'обробляє raw-повідомлення, треки, alerts та incidents'
        'api' = 'публічна карта й read-only HTTP API на порту 8090'
        'admin' = 'приватна панель керування й діагностики на порту 8091'
        'analytics' = 'будує аналітичні індекси та звіти з повідомлень'
        'messaging' = 'RabbitMQ pipeline: relay, parsing, LLM та domain writers'
    }
    $available = @($services.Keys)
    Write-Host "`nЩо публікувати:"
    Write-Host "  0. Увесь стек (усі компоненти, потрібні для повної роботи платформи)"
    for ($i = 0; $i -lt $available.Count; $i++) { Write-Host ("  {0}. {1} ({2})" -f ($i + 1), $available[$i], $services[$available[$i]]) }
    do {
        $answer = (Read-Host "Номери через кому").Trim()
        if ($answer -eq '0') { return @() }
        $numbers = @($answer -split '\s*,\s*' | Where-Object { $_ })
        $valid = $numbers.Count -gt 0 -and @($numbers | Where-Object { $_ -notmatch '^\d+$' -or [int]$_ -lt 1 -or [int]$_ -gt $available.Count }).Count -eq 0
        if (-not $valid) { Write-Warning "Вкажіть 0 або номери від 1 до $($available.Count) через кому."; continue }
        return @($numbers | ForEach-Object { $available[[int]$_ - 1] } | Select-Object -Unique)
    } while ($true)
}

function Invoke-DeploymentWizard {
    Step "Майстер розгортання Puluj-G"
    Write-Host "Значення з app_settings мають пріоритет над .env для runtime-параметрів. Майстер може застосувати введені значення і туди."
    # Preserve an empty selection as a real empty string array: it means the whole stack.
    $script:Services = @((Select-Services) | Where-Object { $_ })
    $script:selectedServicesOnly = $Services.Count -gt 0
    $script:NoBuild = -not (Read-YesNo "Перебудувати вибрані Docker-образи?" $true)

    Write-Host "`nБаза даних:"
    Write-Host "  1. Залишити наявну БД без очищення"
    Write-Host "  2. Створити новий порожній Docker volume, якщо його ще немає"
    Write-Host "  3. Повністю очистити ізольоване розгортання і БД"
    $databaseChoice = Read-Choice "Оберіть 1, 2 або 3" @('1', '2', '3')
    $defaultVolume = $script:DatabaseVolume
    $volumeAnswer = (Read-Host "Назва PostgreSQL volume (Enter — $defaultVolume)").Trim()
    if ($volumeAnswer) { $script:DatabaseVolume = $volumeAnswer }
    switch ($databaseChoice) {
        '1' { }
        '2' { $script:InitializeDatabase = $true }
        '3' {
            Write-Host "УВАГА: буде видалено дані БД, Telegram session, логи й RabbitMQ state лише для '$($script:ComposeProject)'." -ForegroundColor Yellow
            $confirmation = Read-Host "Для підтвердження введіть DELETE $($script:ComposeProject)"
            if ($confirmation -ne "DELETE $($script:ComposeProject)") { throw "Очищення скасовано: фраза підтвердження не збігається." }
            $script:ResetDatabase = $true
            $script:ConfirmReset = $true
        }
    }

    $script:Broker = Read-YesNo "Увімкнути broker profile (RabbitMQ + messaging)?" ([bool]$script:Broker)
    if ($script:Broker) { $script:DomainWriters = Read-YesNo "Передати domain writers у messaging (зупиняє legacy processor)?" ([bool]$script:DomainWriters) }
    else { $script:DomainWriters = $false }
    if ($script:DomainWriters) {
        $script:ProcessorReplicas = 0
        Write-Host "Domain writers увімкнені: processor встановлено в 0, щоб не було двох writer-ів над однією БД." -ForegroundColor Yellow
    } else {
        $script:ProcessorReplicas = Read-ProcessorReplicaCount $script:ProcessorReplicas
    }

    if (Read-YesNo "Ввести або змінити токени й параметри колекторів/LLM зараз?" $false) {
        $current = Get-DotEnvValues
        Step "Конфігурація (Enter зберігає поточне значення)"
        Read-Setting $current 'ADMIN_TOKEN' 'Токен доступу до Admin' $true
        Read-Setting $current 'Collectors__AlertsInUa__Enabled' 'Увімкнути alerts.in.ua (true/false)'
        Read-Setting $current 'Collectors__AlertsInUa__Token' 'Токен alerts.in.ua' $true
        Read-Setting $current 'Collectors__Telegram__Enabled' 'Увімкнути Telegram (true/false)'
        Read-Setting $current 'Collectors__Telegram__ApiId' 'Telegram API ID'
        Read-Setting $current 'Collectors__Telegram__ApiHash' 'Telegram API hash' $true
        Read-Setting $current 'Collectors__Telegram__Phone' 'Номер Telegram у міжнародному форматі'
        Read-Setting $current 'Collectors__Telegram__Password' 'Пароль двофакторного захисту Telegram' $true
        Read-Setting $current 'Collectors__Telegram__SessionPath' 'Шлях до Telegram session у контейнері'
        Read-Setting $current 'Llm__Enabled' 'Увімкнути LLM fallback (true/false)'
        Read-Setting $current 'Llm__Model' 'Модель LLM'
        Read-Setting $current 'ANTHROPIC_API_KEY' 'Anthropic API key' $true
        Read-Setting $current 'OTEL_EXPORTER_OTLP_ENDPOINT' 'OTLP endpoint'
        if ($wizardSettings.Count -gt 0) {
            Update-DotEnv $wizardSettings
            $script:applyWizardSettingsToDatabase = Read-YesNo "Також застосувати runtime-настройки до app_settings цієї БД?" $true
        }
    }
}

$runWizard = $Wizard -or (-not $NonInteractive -and $PSBoundParameters.Count -eq 0)
if ($runWizard) { Invoke-DeploymentWizard }

if ($DomainWriters -and -not $Broker) { throw "-DomainWriters needs -Broker: the writers consume observations.recorded from RabbitMQ (ADR-0009)" }
$expectedVolume = "$ComposeProject-pgdata"
if ($ComposeProject -notmatch '^puluj-g(?:-[a-z0-9][a-z0-9-]*)?$') { throw "ComposeProject '$ComposeProject' is not an isolated Puluj-G project name." }
if ($ResetDatabase -and -not $ConfirmReset) { throw "-ResetDatabase is destructive and requires -ConfirmReset. Nothing was deleted." }
if ($ResetDatabase -and $InitializeDatabase) { throw "Use either -ResetDatabase or -InitializeDatabase, not both." }
if ($ResetDatabase -and $DatabaseVolume.Trim() -ne $expectedVolume) { throw "Reset only accepts the exact database volume '$expectedVolume' for Compose project '$ComposeProject'; refusing '$DatabaseVolume'." }
if ($Services -contains 'messaging' -and -not $Broker) { throw "The 'messaging' service belongs to the broker profile; enable -Broker or select it in the wizard." }
if (($ResetDatabase -or $InitializeDatabase) -and $Services.Count -gt 0 -and $Services -notcontains 'migrate') {
    # A new database must receive schema and seed data even when the operator publishes only one service.
    $Services += 'migrate'
    Write-Host "Додано migrate: порожня або очищена БД спершу має отримати схему й seed-дані." -ForegroundColor Yellow
}
if ($Broker -and $Services.Count -gt 0 -and @($Services | Where-Object { $_ -in @('collector-telegram', 'collector-alerts') }).Count -gt 0 -and $Services -notcontains 'messaging') {
    $Services += 'messaging'
    Write-Host "Додано messaging: колектори у broker-режимі мають передавати дані до єдиного ingress." -ForegroundColor Yellow
}
if ($DomainWriters -and $Services.Count -gt 0 -and $Services -notcontains 'messaging') {
    $Services += 'messaging'
    Write-Host "Додано messaging: він виконує обрані domain writer ролі." -ForegroundColor Yellow
}
$requiresMigrate = $Services.Count -eq 0 -or $Services -contains 'migrate' -or @($Services | Where-Object { $_ -ne 'postgis' }).Count -gt 0
$dockerBin = "C:\Program Files\Docker\Docker\resources\bin"
if (-not (Get-Command docker -ErrorAction SilentlyContinue) -and (Test-Path "$dockerBin\docker.exe")) { $env:PATH = "$env:PATH;$dockerBin" }
if (-not (Get-Command docker -ErrorAction SilentlyContinue)) { throw "docker not found (Docker Desktop is not installed or not in PATH)" }
if ([string]::IsNullOrWhiteSpace($DatabaseVolume)) { throw "DatabaseVolume must not be empty" }

# The Postgres volume is external: Compose must never silently make a fresh database when a volume name was mistyped
# or the persistent disk was not mounted.  A first install is deliberately opt-in via -InitializeDatabase.
$volume = $DatabaseVolume.Trim()
$volumeExists = (& docker volume inspect $volume 2>$null) -and $LASTEXITCODE -eq 0
$env:PULUJ_PGDATA_VOLUME = $volume

function Assert-ResetTarget {
    # A name alone is not ownership. Refuse a reset unless the exact existing postgis container of this Compose
    # project is labelled as such and mounts this exact volume at PostgreSQL's data directory. This check happens
    # before `down`, while the evidence still exists.
    Push-Location $deploy
    try {
        $ids = @(& docker compose -p $composeProject ps -aq postgis | Where-Object { $_ })
    } finally { Pop-Location }
    if ($ids.Count -ne 1) { throw "Reset requires exactly one existing postgis container for compose project '$composeProject'; found $($ids.Count). Refusing an unverified target." }
    $details = @(& docker inspect $ids[0] | ConvertFrom-Json)
    if ($LASTEXITCODE -ne 0 -or $details.Count -ne 1) { throw "Could not inspect postgis container '$($ids[0])'; database was not removed." }
    $labels = $details[0].Config.Labels
    if ($labels.'com.docker.compose.project' -ne $composeProject -or $labels.'com.docker.compose.service' -ne 'postgis') {
        throw "Container '$($ids[0])' is not the postgis service of compose project '$composeProject'; database was not removed."
    }
    $mounts = @($details[0].Mounts | Where-Object { $_.Type -eq 'volume' -and $_.Name -eq $volume -and $_.Destination -eq '/var/lib/postgresql/data' })
    if ($mounts.Count -ne 1) { throw "Postgis container '$($ids[0])' does not mount verified volume '$volume' at /var/lib/postgresql/data; database was not removed." }
}

if ($ResetDatabase) {
    if (-not $volumeExists) { throw "PostgreSQL volume '$volume' does not exist; refusing a reset with an unverified target." }
    Assert-ResetTarget
    Step "Resetting isolated Puluj-G target: compose '$composeProject', database volume '$volume'"
    Push-Location $deploy
    try {
        # --volumes clears only non-external volumes of this exact Compose project. The external database volume is
        # removed explicitly below, after all services that use it have stopped.
        & docker compose -p $composeProject down --volumes --remove-orphans
        if ($LASTEXITCODE -ne 0) { throw "could not stop isolated compose project '$composeProject'; database was not removed" }
    } finally { Pop-Location }
    & docker volume rm $volume | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not remove verified PostgreSQL volume '$volume'; reset stopped before recreation" }
    & docker volume create $volume | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "could not recreate PostgreSQL volume '$volume'; rerun deploy with the same target after resolving Docker's error" }
    $volumeExists = $true
    Write-Host "Database volume recreated. The normal migrate/seed/health sequence follows; do not call reset successful until it completes." -ForegroundColor Yellow
}
elseif (-not $volumeExists) {
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

# Platform path (P03–P09): which roles the `messaging` worker runs and how many legacy processors stay. Compose reads
# these through ${…} substitution, so they are set here per run — the plain run always restores the legacy layout.
$defaultMessagingRoles = "relay,archive,raw-writer,normalizer,parser,llm-worker,finalizer,projection,replay,message-analytics"
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
    $env:PROCESSOR_REPLICAS = "$ProcessorReplicas"
}
function Sql([string]$file) {
    # psql is not installed on the host: the script goes through the postgis container (Cyrillic-safe via stdin).
    Get-Content -Raw -Encoding UTF8 $file | docker exec -i $postgisContainer psql -U puluj -d puluj -v ON_ERROR_STOP=1 -f -
    if ($LASTEXITCODE -ne 0) { throw "psql failed for $file" }
}

function Apply-WizardSettingsToDatabase {
    if (-not $applyWizardSettingsToDatabase -or $wizardSettings.Count -eq 0) { return }
    $keyMap = [ordered]@{
        'ADMIN_TOKEN' = 'Admin:Token'
        'Collectors__AlertsInUa__Enabled' = 'Collectors:AlertsInUa:Enabled'
        'Collectors__AlertsInUa__Token' = 'Collectors:AlertsInUa:Token'
        'Collectors__Telegram__Enabled' = 'Collectors:Telegram:Enabled'
        'Collectors__Telegram__ApiId' = 'Collectors:Telegram:ApiId'
        'Collectors__Telegram__ApiHash' = 'Collectors:Telegram:ApiHash'
        'Collectors__Telegram__Phone' = 'Collectors:Telegram:Phone'
        'Collectors__Telegram__Password' = 'Collectors:Telegram:Password'
        'Llm__Enabled' = 'Llm:Enabled'
        'Llm__Model' = 'Llm:Model'
        'ANTHROPIC_API_KEY' = 'Llm:ApiKey'
    }
    $secretKeys = @('Admin:Token', 'Collectors:AlertsInUa:Token', 'Collectors:Telegram:ApiHash', 'Collectors:Telegram:Password', 'Llm:ApiKey')
    $statements = [System.Collections.Generic.List[string]]::new()
    foreach ($entry in $keyMap.GetEnumerator()) {
        if (-not $wizardSettings.Contains($entry.Key)) { continue }
        $key = $entry.Value
        $value = [string]$wizardSettings[$entry.Key]
        if ([string]::IsNullOrEmpty($value)) {
            $statements.Add("DELETE FROM app_settings WHERE key = '$key';")
            continue
        }
        $base64 = [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($value))
        $isSecret = if ($key -in $secretKeys) { 'true' } else { 'false' }
        $statements.Add("INSERT INTO app_settings (key, value, is_secret, updated_at) VALUES ('$key', convert_from(decode('$base64', 'base64'), 'UTF8'), $isSecret, now()) ON CONFLICT (key) DO UPDATE SET value = EXCLUDED.value, is_secret = EXCLUDED.is_secret, updated_at = EXCLUDED.updated_at;")
    }
    if ($statements.Count -eq 0) { return }
    Step "Applying runtime configuration to app_settings"
    $statements | docker exec -i $postgisContainer psql -U puluj -d puluj -v ON_ERROR_STOP=1 -f - | Out-Null
    if ($LASTEXITCODE -ne 0) { throw "Could not apply wizard runtime settings to app_settings." }
    Write-Host "Runtime-настройки застосовано до app_settings; секрети не показані." -ForegroundColor Green
}

function ComposeContainerId([string]$service) {
    $ids = @(& docker compose -p $composeProject ps -aq $service | Where-Object { $_ })
    if ($ids.Count -ne 1) { throw "Expected exactly one $service container in compose project '$composeProject', found $($ids.Count)." }
    return $ids[0].Trim()
}

# A local dev-run Worker next to Docker processors means two processor versions over one database and two Telegram
# clients on one session. A database-only/migrate-only selection does not touch unrelated local development processes.
$startsApplication = $Services.Count -eq 0 -or @($Services | Where-Object { $_ -notin @('postgis', 'migrate') }).Count -gt 0
if ($startsApplication) {
    $local = Get-Process -Name "Puluj.Worker", "Puluj.Api", "Puluj.Admin", "Puluj.Analytics.Worker" -ErrorAction SilentlyContinue
    if ($local) {
        Step "Stopping local dev-run processes ($($local.Name -join ', '))"
        $local | Stop-Process -Force
    }
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

    if ($requiresMigrate) {
        Step "Waiting for the migrate service (migrations + seed)"
        $deadline = (Get-Date).AddMinutes(30)
        do {
            $migrateContainer = ComposeContainerId "migrate"
            $state = docker inspect --format '{{.State.Status}} {{.State.ExitCode}}' $migrateContainer 2>$null
            if ($state -like "exited 0*") { break }
            if ($state -like "exited *") { docker logs --tail 50 $migrateContainer; throw "migrate exited with $state" }
            Start-Sleep 5
        } while ((Get-Date) -lt $deadline)
        if ($state -notlike "exited 0*") { throw "Timed out waiting for migrate (last state: $state)." }
        Write-Host "migrate: $state"
        docker logs $migrateContainer 2>&1 | Select-String -Pattern "Applying|migration|Seeding" | Select-Object -Last 8
    }

    Step "Containers"
    docker compose -p $composeProject @profileArgs ps --format "table {{.Name}}\t{{.Service}}\t{{.Status}}\t{{.Image}}"
    $postgisContainer = ComposeContainerId "postgis"
    $processorContainers = @(& docker compose -p $composeProject ps -q processor | Where-Object { $_ })
    if ($DomainWriters -and $processorContainers.Count -gt 0) { throw "processor containers are still running after the cutover: $($processorContainers -join ', ')" }
    $messagingContainers = @(& docker compose -p $composeProject @profileArgs ps -q messaging | Where-Object { $_ })
} finally { Pop-Location }

if ($requiresMigrate) { Apply-WizardSettingsToDatabase }

if (-not $SkipSql -and $requiresMigrate) {
    Step "One-off SQL: Failed raw messages back to Pending (deadlock victims of 15.09)"
    Sql (Join-Path $root "scripts\requeue-failed.sql")
    Step "One-off SQL: text alerts closed by an out-of-order 'відбій' (ended_at < started_at) reopened for the watchdog"
    Sql (Join-Path $root "scripts\fix-text-alert-ends.sql")
} elseif (-not $SkipSql) { Write-Host "Пропущено SQL-корекції: обрано лише postgis, без migrate/schema check." -ForegroundColor Yellow }

Step "Checks"
if ($processorContainers.Count -gt 0 -or $messagingContainers.Count -gt 0) { Start-Sleep 20 }  # let active workers claim messages
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
foreach ($u in @(
    if ($Services.Count -eq 0 -or $Services -contains 'api') { 'http://localhost:8090/api/health' }
    if ($Services.Count -eq 0 -or $Services -contains 'admin') { 'http://localhost:8091/api/health' }
)) {
    try { $r = Invoke-WebRequest -UseBasicParsing -TimeoutSec 10 $u; Write-Host ("{0} -> {1}" -f $u, $r.StatusCode) }
    catch { Write-Warning "$u -> $($_.Exception.Message)" }
}
if ($requiresMigrate) {
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
}
Write-Host "`nГотово. Map: http://localhost:8090  Admin: http://localhost:8091" -ForegroundColor Green
