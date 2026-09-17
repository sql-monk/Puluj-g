[CmdletBinding()]
param(
    [string]$DocsPath = 'docs'
)

$ErrorActionPreference = 'Stop'
$repoRoot = (Resolve-Path (Join-Path $PSScriptRoot '../..')).Path
$docsRoot = Join-Path $repoRoot $DocsPath

if (-not (Test-Path -LiteralPath $docsRoot -PathType Container)) {
    throw "Documentation directory does not exist: $DocsPath"
}

$errors = [System.Collections.Generic.List[string]]::new()
$pngReferences = [System.Collections.Generic.HashSet[string]]::new([System.StringComparer]::OrdinalIgnoreCase)
$serviceArtifactRoots = @(
    (Join-Path $docsRoot 'evidence'),
    (Join-Path $docsRoot 'fixtures')
)

function Test-ActiveDocumentationPath {
    param([string]$Path)

    foreach ($serviceArtifactRoot in $serviceArtifactRoots) {
        $serviceArtifactPrefix = $serviceArtifactRoot + [System.IO.Path]::DirectorySeparatorChar
        if ($Path.Equals($serviceArtifactRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            $Path.StartsWith($serviceArtifactPrefix, [System.StringComparison]::OrdinalIgnoreCase)) {
            return $false
        }
    }
    return $true
}

$drawioFiles = Get-ChildItem -LiteralPath $docsRoot -Filter '*.drawio' -File -Recurse |
    Where-Object { Test-ActiveDocumentationPath $_.FullName }
$pngFiles = Get-ChildItem -LiteralPath $docsRoot -Filter '*.png' -File -Recurse |
    Where-Object { Test-ActiveDocumentationPath $_.FullName }
$markdownFiles = Get-ChildItem -LiteralPath $docsRoot -Filter '*.md' -File -Recurse |
    Where-Object { Test-ActiveDocumentationPath $_.FullName }

foreach ($drawio in $drawioFiles) {
    $expectedPng = [System.IO.Path]::ChangeExtension($drawio.FullName, '.png')
    if (-not (Test-Path -LiteralPath $expectedPng -PathType Leaf)) {
        $errors.Add("Missing PNG pair for $([System.IO.Path]::GetRelativePath($repoRoot, $drawio.FullName))")
    }
}

foreach ($png in $pngFiles) {
    $expectedDrawio = [System.IO.Path]::ChangeExtension($png.FullName, '.drawio')
    if (-not (Test-Path -LiteralPath $expectedDrawio -PathType Leaf)) {
        $errors.Add("Missing Draw.io pair for $([System.IO.Path]::GetRelativePath($repoRoot, $png.FullName))")
    }
}

$linkPattern = '(?m)(?<!!)(?:!?)\[[^\]]*\]\((?<target><[^>]+>|[^\s)]+)(?:\s+[^)]*)?\)|^\s*\[[^\]]+\]:\s*(?<target><[^>]+>|\S+)'
foreach ($markdown in $markdownFiles) {
    $content = Get-Content -LiteralPath $markdown.FullName -Raw
    foreach ($match in [regex]::Matches($content, $linkPattern)) {
        $target = $match.Groups['target'].Value.Trim('<>')
        if ([string]::IsNullOrWhiteSpace($target) -or $target.Contains('<') -or $target.Contains('>') -or $target.StartsWith('#') -or $target -match '^[a-zA-Z][a-zA-Z0-9+.-]*:') {
            continue
        }

        $target = [uri]::UnescapeDataString(($target -split '[#?]', 2)[0])
        if ([string]::IsNullOrWhiteSpace($target)) {
            continue
        }

        $resolved = [System.IO.Path]::GetFullPath((Join-Path $markdown.DirectoryName $target))
        if (-not (Test-Path -LiteralPath $resolved)) {
            $errors.Add("Broken local link in $([System.IO.Path]::GetRelativePath($repoRoot, $markdown.FullName)): $target")
            continue
        }

        if ([System.IO.Path]::GetExtension($resolved) -ieq '.png') {
            [void]$pngReferences.Add($resolved)
        }
    }
}

foreach ($png in $pngFiles) {
    if (-not $pngReferences.Contains($png.FullName)) {
        $errors.Add("Unreferenced PNG: $([System.IO.Path]::GetRelativePath($repoRoot, $png.FullName))")
    }
}

if ($errors.Count -gt 0) {
    $errors | ForEach-Object { [Console]::Error.WriteLine($_) }
    throw "Documentation validation failed with $($errors.Count) error(s)."
}

Write-Host "Documentation validation passed: $($markdownFiles.Count) Markdown file(s), $($drawioFiles.Count) Draw.io file(s), $($pngFiles.Count) PNG file(s)."
