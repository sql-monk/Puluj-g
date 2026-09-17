<#
.SYNOPSIS
Runs U13's non-mocked browser acceptance gate against a disposable PostGIS database.

.DESCRIPTION
The runner creates one Docker container, migrates and seeds it through the real Worker,
starts the actual API with the built public SPA, then runs only E13-actual-api.e2e.ts.
It never reads PULUJ_TEST_CONNECTION and removes its precisely named container on every
outcome unless -KeepEnvironment is explicitly selected for local investigation.
#>
param(
    [string]$OutputDirectory = '../artifacts/u13-actual',
    [switch]$KeepEnvironment
)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$output = [IO.Path]::GetFullPath((Join-Path $root $OutputDirectory))
$containerName = "puluj-u13-actual-$PID"
$saved = @{}
foreach ($name in @('ConnectionStrings__Puluj', 'Worker__Roles', 'ASPNETCORE_ENVIRONMENT', 'E13_ACTUAL_BASE_URL', 'E13_SCREENSHOT_PATH', 'PLAYWRIGHT_HTML_OUTPUT_DIR')) {
    $saved[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
$outcomes = [System.Collections.Generic.List[object]]::new()
$api = $null
$succeeded = $false

function Invoke-Gate([string[]]$Command) {
    $started = [DateTime]::UtcNow
    & $Command[0] $Command[1..($Command.Length - 1)]
    $exit = $LASTEXITCODE
    $outcomes.Add([ordered]@{ command = $Command -join ' '; startedAtUtc = $started.ToString('O'); finishedAtUtc = [DateTime]::UtcNow.ToString('O'); exitCode = $exit })
    if ($exit -ne 0) { throw "U13 actual gate failed ($exit): $($Command -join ' ')" }
}

function Require([bool]$Condition, [string]$Message) {
    if (-not $Condition) { throw "U13 actual acceptance failed: $Message" }
}

function Read-Public([string]$Path) {
    return Invoke-RestMethod -Uri "$env:E13_ACTUAL_BASE_URL$Path" -TimeoutSec 20
}

try {
    New-Item -ItemType Directory -Force -Path $output | Out-Null
    Invoke-Gate @('docker', 'run', '--detach', '--rm', '--name', $containerName, '-e', 'POSTGRES_USER=puluj', '-e', 'POSTGRES_PASSWORD=puluj', '-e', 'POSTGRES_DB=puluj_test', '-p', '127.0.0.1::5432', 'postgis/postgis:17-3.5')
    $databaseReady = $false
    for ($attempt = 0; $attempt -lt 60 -and -not $databaseReady; $attempt++) {
        Start-Sleep -Seconds 1
        & docker exec $containerName pg_isready -U puluj -d puluj_test *> $null
        $databaseReady = $LASTEXITCODE -eq 0
    }
    Require $databaseReady 'disposable PostGIS did not become ready before migration'
    $mapping = (& docker port $containerName '5432/tcp')
    if ($LASTEXITCODE -ne 0) { throw 'Docker did not return the disposable PostGIS port.' }
    $portMatch = [regex]::Match(($mapping | Select-Object -First 1), ':(\d+)$')
    Require $portMatch.Success "cannot parse PostGIS port from '$mapping'"
    $env:ConnectionStrings__Puluj = "Host=127.0.0.1;Port=$($portMatch.Groups[1].Value);Database=puluj_test;Username=puluj;Password=puluj"
    $env:ASPNETCORE_ENVIRONMENT = 'Development'

    Push-Location $root
    try {
        $env:Worker__Roles = 'migrate'
        Invoke-Gate @('dotnet', 'run', '--no-build', '--project', 'src/Puluj.Worker/Puluj.Worker.csproj')
        Remove-Item Env:Worker__Roles -ErrorAction SilentlyContinue
        Invoke-Gate @('dotnet', 'run', '--no-build', '--project', 'tools/Puluj.U13FixtureSeeder/Puluj.U13FixtureSeeder.csproj')
        # A worktree may intentionally have no prior API output.  Build after the database is ready so the subsequent
        # `--no-build` process proves it is launching the exact compiled public assets, not an accidental Vite server.
        Invoke-Gate @('dotnet', 'build', 'src/Puluj.Api/Puluj.Api.csproj', '--no-restore')

        Push-Location (Join-Path $root 'web')
        try {
            Invoke-Gate @('npm', 'ci')
            Invoke-Gate @('npm', 'run', 'build:user')
        } finally {
            Pop-Location
        }

        $listener = [System.Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        $listener.Start(); $apiPort = ([Net.IPEndPoint]$listener.LocalEndpoint).Port; $listener.Stop()
        $env:E13_ACTUAL_BASE_URL = "http://127.0.0.1:$apiPort"
        $apiLog = Join-Path $output 'api.log'
        $apiErrorLog = Join-Path $output 'api-error.log'
        $apiDll = Join-Path $root 'src/Puluj.Api/bin/Debug/net10.0/Puluj.Api.dll'
        Require (Test-Path $apiDll) "API build did not produce $apiDll"
        $api = Start-Process -FilePath 'dotnet' -ArgumentList @($apiDll, '--urls', $env:E13_ACTUAL_BASE_URL) -WorkingDirectory $root -WindowStyle Hidden -RedirectStandardOutput $apiLog -RedirectStandardError $apiErrorLog -PassThru

        $ready = $false
        for ($attempt = 0; $attempt -lt 60 -and -not $ready; $attempt++) {
            Start-Sleep -Seconds 1
            try { $ready = (Invoke-WebRequest -Uri "$env:E13_ACTUAL_BASE_URL/api/health" -UseBasicParsing -TimeoutSec 2).StatusCode -eq 200 } catch { }
        }
        Require $ready "API health did not become 200; inspect $apiLog and $apiErrorLog"

        $windowFrom = [uri]::EscapeDataString(([DateTimeOffset]::UtcNow.AddHours(-1)).ToString('O'))
        $windowTo = [uri]::EscapeDataString(([DateTimeOffset]::UtcNow.AddMinutes(1)).ToString('O'))
        $messageWindow = "from=$windowFrom&to=$windowTo&pageSize=100"
        $messages = Read-Public "/api/public/messages?$messageWindow"
        Require ($messages.items.Count -eq 100) 'the real public messages endpoint did not return the first 100-item page'
        Require (-not [string]::IsNullOrWhiteSpace($messages.nextCursor)) 'the real public messages endpoint did not return a continuation cursor'
        $next = Read-Public ("/api/public/messages?$messageWindow&cursor=" + [uri]::EscapeDataString($messages.nextCursor))
        $revision = @($messages.items + $next.items | Where-Object { $_.sourceMessageKey -eq 'revision' -and $_.sourceRevision -eq '0' } | Select-Object -First 1)
        Require ($revision.Count -eq 1) 'fixture revision was not returned by real public paging'
        $revisions = Read-Public ("/api/public/messages/$($revision[0].id)/revisions")
        Require ($revisions.totalCount -eq 2) 'the same-source revision chain was not returned by the real API'
        $results = Read-Public ("/api/public/messages/$($revision[0].id)/results")
        Require (@($results.items | Where-Object { $_.targetId -eq '9007199254740993' }).Count -eq 1) 'the API did not preserve the exact 64-bit target identifier'
        $entities = Read-Public '/api/public/entities?pageSize=100'
        $kinds = @($entities.items | ForEach-Object kind)
        foreach ($kind in @('track', 'incident', 'alert')) { Require ($kinds -contains $kind) "the real entity catalogue is missing $kind" }
        $active = Read-Public '/api/public/entities?entityKinds=track,incident,alert&status=active&pageSize=100'
        Require ($active.items.Count -gt 0) 'the real active entity filter returned no fixture data'
        $track = @($entities.items | Where-Object kind -eq 'track' | Select-Object -First 1)
        Require ($track.Count -eq 1) 'the real track fixture is unavailable'
        $historicalAt = [uri]::EscapeDataString(([DateTimeOffset]::UtcNow).ToString('O'))
        $historical = Read-Public ("/api/public/entities/track/$($track[0].id)?historyBasis=reconstructed&at=$historicalAt")
        Require ($historical.entity.state -eq 'active') 'the real historical track detail did not reconstruct the active fixture'

        $env:E13_SCREENSHOT_PATH = Join-Path $output 'actual-browser.png'
        $env:PLAYWRIGHT_HTML_OUTPUT_DIR = Join-Path $output 'playwright-report'
        Push-Location (Join-Path $root 'web')
        try {
            Invoke-Gate @('npx', 'playwright', 'test', 'e2e/E13-actual-api.e2e.ts', '--project', 'desktop', '--output', (Join-Path $output 'playwright'))
        } finally {
            Pop-Location
        }
        $succeeded = $true
    } finally {
        Pop-Location
    }
} finally {
    if ($api -and -not $api.HasExited) { Stop-Process -Id $api.Id -Force }
    if (-not $KeepEnvironment) { & docker rm -f $containerName *> $null }
    $manifest = [ordered]@{
        task = 'U13-B1'; commit = (git -C $root rev-parse HEAD).Trim(); completedAtUtc = [DateTime]::UtcNow.ToString('O'); succeeded = $succeeded
        disposableDatabase = $true; apiBaseUrl = $env:E13_ACTUAL_BASE_URL; commands = @($outcomes)
        artifacts = @('api.log', 'api-error.log', 'actual-browser.png', 'playwright-report')
    }
    $manifest | ConvertTo-Json -Depth 5 | Set-Content -Encoding utf8 (Join-Path $output 'manifest.json')
    foreach ($pair in $saved.GetEnumerator()) {
        if ($null -eq $pair.Value) { Remove-Item "Env:$($pair.Key)" -ErrorAction SilentlyContinue } else { Set-Item "Env:$($pair.Key)" $pair.Value }
    }
}
