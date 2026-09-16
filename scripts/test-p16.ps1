<#
.SYNOPSIS
Runs the P16 release gate against disposable Testcontainers PostGIS and RabbitMQ.

.DESCRIPTION
The script deliberately clears PULUJ_TEST_CONNECTION so the integration suite cannot truncate a developer or
production database.  A missing Docker daemon is a failure, not a green skipped gate.  It keeps TRX output under
docs/evidence/message-platform/test-results; the P16 integration tests write committed-outcome evidence to their
own P16-release-evidence.json file and never overwrite P03–P15 evidence.

Use -SkipWeb only when the runner cannot host Playwright.  That is an explicit skipped release check and must be
recorded in the P16 handoff; CI and a release candidate run without it.
#>
param([switch]$SkipWeb)

$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$evidence = Join-Path $root 'docs/evidence/message-platform/test-results'
$savedConnection = $env:PULUJ_TEST_CONNECTION
$savedReset = $env:PULUJ_TEST_ALLOW_RESET

function Invoke-Gate([string[]]$Command) {
    & pwsh -File (Join-Path $root 'scripts/with-lock.ps1') @Command
    if ($LASTEXITCODE -ne 0) { throw "P16 gate command failed ($LASTEXITCODE): $($Command -join ' ')" }
}

try {
    New-Item -ItemType Directory -Force -Path $evidence | Out-Null
    # PipelineFixture and MessagingFixture must use disposable containers, never a supplied connection string.
    Remove-Item Env:PULUJ_TEST_CONNECTION -ErrorAction SilentlyContinue
    Remove-Item Env:PULUJ_TEST_ALLOW_RESET -ErrorAction SilentlyContinue

    Push-Location $root
    try {
        Invoke-Gate @('dotnet', 'test', 'tests/Puluj.Messaging.Tests/Puluj.Messaging.Tests.csproj', '--filter',
            'FullyQualifiedName~P16ReleaseGateTests|FullyQualifiedName~CrashTests|FullyQualifiedName~DomainWriterTests|FullyQualifiedName~FinalizerTests|FullyQualifiedName~ReplayTests|FullyQualifiedName~LifecycleTests',
            '--logger', 'trx;LogFileName=p16-messaging.trx', '--results-directory', $evidence)
        Invoke-Gate @('dotnet', 'test', 'tests/Puluj.Messaging.Contracts.Tests/Puluj.Messaging.Contracts.Tests.csproj',
            '--logger', 'trx;LogFileName=p16-contracts.trx', '--results-directory', $evidence)
        Invoke-Gate @('dotnet', 'test', 'tests/Puluj.Integration.Tests/Puluj.Integration.Tests.csproj',
            '--logger', 'trx;LogFileName=p16-postgis.trx', '--results-directory', $evidence)
        Invoke-Gate @('dotnet', 'build', 'Puluj.sln')

        if (-not $SkipWeb) {
            Push-Location (Join-Path $root 'web')
            try {
                Invoke-Gate @('npm', 'ci')
                Invoke-Gate @('npm', 'run', 'lint')
                Invoke-Gate @('npm', 'test', '--', '--run')
                Invoke-Gate @('npx', 'playwright', 'test')
            } finally { Pop-Location }
        }
    } finally { Pop-Location }
} finally {
    $env:PULUJ_TEST_CONNECTION = $savedConnection
    $env:PULUJ_TEST_ALLOW_RESET = $savedReset
}
