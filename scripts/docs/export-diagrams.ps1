[CmdletBinding()]
param(
    [string]$DiagramPath = 'docs/diagrams',
    [string]$DrawioCommand = 'drawio'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$diagramRoot = Join-Path $repoRoot $DiagramPath

if (-not (Test-Path -LiteralPath $diagramRoot -PathType Container)) {
    throw "Diagram directory does not exist: $DiagramPath"
}

$diagrams = Get-ChildItem -LiteralPath $diagramRoot -Filter '*.drawio' -File -Recurse
if ($diagrams.Count -eq 0) {
    Write-Host "No Draw.io files found in $DiagramPath."
    exit 0
}

foreach ($diagram in $diagrams) {
    $pngPath = [System.IO.Path]::ChangeExtension($diagram.FullName, '.png')
    & $DrawioCommand --export --format png --transparent --border 0 --scale 2 --output $pngPath $diagram.FullName
    if ($LASTEXITCODE -ne 0) {
        throw "Draw.io export failed for $($diagram.FullName) with exit code $LASTEXITCODE."
    }
    if (-not (Test-Path -LiteralPath $pngPath -PathType Leaf)) {
        throw "Draw.io did not produce expected PNG: $pngPath"
    }
    Write-Host "Exported $([System.IO.Path]::GetRelativePath($repoRoot, $pngPath))"
}
