[CmdletBinding()]
param(
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$Capture
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'tests\Nvt.Replay.Avalonia.Tests\Nvt.Replay.Avalonia.Tests.csproj'
$candidateDirectory = Join-Path $repoRoot 'artifacts\ui-snapshot-candidates'
$buildOutputName = 'bin-ui-candidates'
$testFilter = 'FullyQualifiedName~Candidate_snapshot_matrix_covers_primary_workspaces_themes_and_widths'

if (-not $Capture) {
    throw 'Candidate capture replaces local review artifacts. Re-run with -Capture to opt in explicitly.'
}

function Remove-IsolatedBuildOutputs {
    Get-ChildItem -LiteralPath $repoRoot -Directory -Recurse -Filter $buildOutputName -ErrorAction SilentlyContinue |
        ForEach-Object {
            $resolved = [System.IO.Path]::GetFullPath($_.FullName)
            $repoPrefix = $repoRoot.TrimEnd('\') + '\'
            if (-not $resolved.StartsWith($repoPrefix, [System.StringComparison]::OrdinalIgnoreCase) -or
                [System.IO.Path]::GetFileName($resolved) -ne $buildOutputName) {
                throw "Refusing to remove unexpected build path: $resolved"
            }
            Remove-Item -LiteralPath $resolved -Recurse -Force
        }
}

try {
    $env:NVT_UI_CANDIDATE_DIR = $candidateDirectory
    $env:NVT_UI_CANDIDATE_MODE = 'capture'
    dotnet test $project -c $Configuration --nologo --filter $testFilter "-p:BaseOutputPath=$buildOutputName\"
    if ($LASTEXITCODE -ne 0) {
        throw "UI snapshot candidate capture failed with exit code $LASTEXITCODE."
    }

    $snapshots = Get-ChildItem -LiteralPath $candidateDirectory -File -Filter '*.png'
    $totalBytes = ($snapshots | Measure-Object Length -Sum).Sum
    Write-Host "Captured local snapshot candidates: $($snapshots.Count)"
    Write-Host "Candidate bytes: $totalBytes"
    Write-Host "Review directory (git-ignored): $candidateDirectory"
    Write-Host 'These are candidates, not approved visual baselines.'
}
finally {
    Remove-Item Env:NVT_UI_CANDIDATE_DIR,Env:NVT_UI_CANDIDATE_MODE -ErrorAction SilentlyContinue
    dotnet clean $project -c $Configuration --nologo "-p:BaseOutputPath=$buildOutputName\" | Out-Null
    Remove-IsolatedBuildOutputs
}
