<#
.SYNOPSIS  Rebuilds and (re)starts Puluj.Worker, Puluj.Api (map, :5267), Puluj.Admin (admin panel, :5268) and
           Puluj.Analytics.Worker (source analytics, :5269) in the background for local development. Console output:
           $env:TEMP\puluj-g-{worker,api,admin,analytics}.log; the services also
           write rolling files into <repo>\logs\ (the admin panel reads them). Use -Stop to only stop them.
           -Public builds both SPAs into the wwwroot folders and binds Api and Admin to all interfaces (LAN access,
           no Vite needed); the firewall rules for 5267/5268 are added once and need an elevated shell.
#>
param([switch]$Stop, [switch]$ResetDb, [switch]$Public, [switch]$Force)
$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot\..").Path
# Do not stop a sibling Puluj checkout: the executable names are identical, so only
# terminate processes whose command line belongs to this exact working tree.
$ownProcesses = Get-CimInstance Win32_Process -ErrorAction SilentlyContinue |
    Where-Object { $_.Name -in "Puluj.Api.exe", "Puluj.Worker.exe", "Puluj.Admin.exe", "Puluj.Analytics.Worker.exe", "dotnet.exe" -and $_.CommandLine -like "*$root*" }
$ownProcesses | ForEach-Object { Stop-Process -Id $_.ProcessId -Force -ErrorAction SilentlyContinue }
if ($Stop) { return }

# The compose stack (deploy/) exposes its database on :5442. A local Worker next to its processor is only safe
# when both are the same build (the store lock exists since AddRawMessageClaims; an older processor deadlocks the newer
# one), and next to its Telegram collector it is never safe (one Telegram session). -Force skips the question.
$docker = Get-Command docker -ErrorAction SilentlyContinue
if (-not $docker -and (Test-Path "C:\Program Files\Docker\Docker\resources\bin\docker.exe")) { $docker = "C:\Program Files\Docker\Docker\resources\bin\docker.exe" }
if ($docker -and -not $Force) {
    $running = @(& $docker ps --format "{{.Names}}" 2>$null | Where-Object { $_ -match "^puluj-g-(processor|collector-telegram)" })
    if ($running.Count -gt 0) {
        Write-Warning "Docker containers over the same database are running: $($running -join ', ')."
        Write-Warning "A local Worker with the processing role must be the same build as puluj-g-processor-* (else deadlocks); the telegram role clashes with puluj-g-collector-telegram-* (one session). Stop them (cd deploy; docker compose -p puluj-g stop processor collector-telegram) or run with -Force."
        $answer = Read-Host "Start anyway? [y/N]"
        if ($answer -notmatch "^[yY]") { return }
    }
}
if ($ResetDb) {
    $env:PGPASSWORD = "puluj"
    psql -h localhost -p 5442 -U puluj -d postgres -q -c "DROP DATABASE IF EXISTS puluj;" -c "CREATE DATABASE puluj;"
    psql -h localhost -p 5442 -U puluj -d puluj -q -c "CREATE EXTENSION IF NOT EXISTS postgis;"
}
dotnet build "$root\Puluj.sln" | Select-String -Pattern " error |Build succeeded" | Select-Object -Unique
New-Item -ItemType Directory -Force "$root\logs" | Out-Null
$env:DOTNET_ENVIRONMENT = "Development"; $env:ASPNETCORE_ENVIRONMENT = "Development"
$apiUrl = "http://localhost:5267"
$adminUrl = "http://localhost:5268"

function Add-FirewallRuleOnce([string]$name, [int]$port) {
    if (-not (Get-NetFirewallRule -DisplayName $name -ErrorAction SilentlyContinue)) {
        try { New-NetFirewallRule -DisplayName $name -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow | Out-Null }
        catch { Write-Warning "Firewall rule not added (run once as admin): New-NetFirewallRule -DisplayName '$name' -Direction Inbound -Protocol TCP -LocalPort $port -Action Allow" }
    }
}

if ($Public) {
    Push-Location "$root\web"; try { npm run build | Select-String -Pattern "error|built in" } finally { Pop-Location }
    Add-FirewallRuleOnce "Puluj-G API" 5267
    Add-FirewallRuleOnce "Puluj-G Admin" 5268
    # Interface behind the default route = the one other machines on the LAN reach us through (skips WSL/Hyper-V vNICs).
    $ifIndex = (Get-NetRoute -DestinationPrefix "0.0.0.0/0" -ErrorAction SilentlyContinue | Sort-Object RouteMetric, InterfaceMetric | Select-Object -First 1).InterfaceIndex
    $lanIp = if ($ifIndex) { (Get-NetIPAddress -AddressFamily IPv4 -InterfaceIndex $ifIndex | Select-Object -First 1).IPAddress }
    if ($lanIp) { $apiUrl = "http://${lanIp}:5267"; $adminUrl = "http://${lanIp}:5268" }
    # launchSettings.json would pin the services back to localhost, so the launch profiles are skipped when binding publicly.
    $apiArgs = "run --no-build --no-launch-profile --project `"$root\src\Puluj.Api`""
    $adminArgs = "run --no-build --no-launch-profile --project `"$root\src\Puluj.Admin`""
    $apiBind = "http://0.0.0.0:5267"; $adminBind = "http://0.0.0.0:5268"
} else {
    $apiArgs = "run --no-build --project `"$root\src\Puluj.Api`""
    $adminArgs = "run --no-build --project `"$root\src\Puluj.Admin`""
    $apiBind = $null; $adminBind = $null
}
Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
# The Worker applies migrations (including the puluj_reader / puluj_admin roles the other two services log in with).
Start-Process dotnet -ArgumentList "run --no-build --project `"$root\src\Puluj.Worker`"" -RedirectStandardOutput "$env:TEMP\puluj-g-worker.log" -RedirectStandardError "$env:TEMP\puluj-g-worker.err" -WindowStyle Hidden
Start-Sleep 3
if ($apiBind) { $env:ASPNETCORE_URLS = $apiBind }
Start-Process dotnet -ArgumentList $apiArgs -RedirectStandardOutput "$env:TEMP\puluj-g-api.log" -RedirectStandardError "$env:TEMP\puluj-g-api.err" -WindowStyle Hidden
if ($adminBind) { $env:ASPNETCORE_URLS = $adminBind } else { Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue }
Start-Process dotnet -ArgumentList $adminArgs -RedirectStandardOutput "$env:TEMP\puluj-g-admin.log" -RedirectStandardError "$env:TEMP\puluj-g-admin.err" -WindowStyle Hidden
# Source analytics service (own schema, :5269); the admin panel's "Аналітика" page reads what it builds.
$env:ASPNETCORE_URLS = "http://localhost:5269"
Start-Process dotnet -ArgumentList "run --no-build --no-launch-profile --project `"$root\src\Puluj.Analytics.Worker`"" -RedirectStandardOutput "$env:TEMP\puluj-g-analytics.log" -RedirectStandardError "$env:TEMP\puluj-g-analytics.err" -WindowStyle Hidden
Remove-Item Env:ASPNETCORE_URLS -ErrorAction SilentlyContinue
Write-Host "Started. Map: $apiUrl  Admin: $adminUrl  Frontend dev: cd web; npm run dev (map) / npm run dev:admin (admin)"
