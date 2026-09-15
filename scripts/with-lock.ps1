<#
.SYNOPSIS  Runs one command under a machine-wide mutex, so parallel agents (or terminals) do not build the same
           working tree at once: dotnet build/test and the Vite build share obj/, bin/, tsbuildinfo and wwwroot.
           Usage: pwsh -File scripts/with-lock.ps1 dotnet build src/Puluj.Admin/Puluj.Admin.csproj
                  pwsh -File scripts/with-lock.ps1 npm run build
           Waits up to 20 minutes for the lock; exits with the command's exit code.
#>
param([Parameter(Mandatory = $true, ValueFromRemainingArguments = $true)][string[]]$Command)
$ErrorActionPreference = "Stop"
$mutex = New-Object System.Threading.Mutex($false, "Global\PulujG.Build")
$acquired = $false
try {
    try { $acquired = $mutex.WaitOne([TimeSpan]::FromMinutes(20)) }
    catch [System.Threading.AbandonedMutexException] { $acquired = $true } # the previous holder died: the lock is ours
    if (-not $acquired) { Write-Error "with-lock: could not acquire Global\PulujG.Build within 20 minutes"; exit 75 }
    $exe = $Command[0]
    $args = if ($Command.Length -gt 1) { $Command[1..($Command.Length - 1)] } else { @() }
    & $exe @args
    exit $LASTEXITCODE
} finally {
    if ($acquired) { $mutex.ReleaseMutex() }
    $mutex.Dispose()
}
