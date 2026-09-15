<# Runs the P00 races and optional load baseline against an automatically disposed Testcontainers PostGIS. #>
param([switch]$Baseline)
$ErrorActionPreference = 'Stop'
$repoPath = Split-Path $PSScriptRoot -Parent
$evidencePath = Join-Path $repoPath 'docs/evidence/message-platform'
$savedConnection = $env:PULUJ_TEST_CONNECTION
$savedBaseline = $env:PULUJ_RUN_BASELINE
$savedEvidence = $env:PULUJ_EVIDENCE_DIRECTORY
try {
    # Never inherit a connection to a development/production database for destructive integration tests.
    Remove-Item Env:PULUJ_TEST_CONNECTION -ErrorAction SilentlyContinue
    $env:PULUJ_RUN_BASELINE = if ($Baseline) { '1' } else { '0' }
    $env:PULUJ_EVIDENCE_DIRECTORY = $evidencePath
    Push-Location $repoPath
    try {
        & pwsh -File scripts/with-lock.ps1 dotnet test tests/Puluj.Integration.Tests/Puluj.Integration.Tests.csproj --filter 'FullyQualifiedName~P00' --logger 'trx;LogFileName=p00-final.trx' --results-directory "$evidencePath/test-results"
        $result = $LASTEXITCODE
    } finally { Pop-Location }
} finally {
    $env:PULUJ_TEST_CONNECTION = $savedConnection
    $env:PULUJ_RUN_BASELINE = $savedBaseline
    $env:PULUJ_EVIDENCE_DIRECTORY = $savedEvidence
}
exit $result
