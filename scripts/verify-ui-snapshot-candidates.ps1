[CmdletBinding()]
param(
    [ValidateRange(1, 10)]
    [int]$Runs = 2,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$KeepAuditOutput
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'tests\Nvt.Replay.Avalonia.Tests\Nvt.Replay.Avalonia.Tests.csproj'
$candidateDirectory = Join-Path $repoRoot 'artifacts\ui-snapshot-candidates'
$auditRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('nvt-ui-candidate-' + [Guid]::NewGuid().ToString('N'))
$buildOutputName = 'bin-ui-candidates'
$testFilter = 'FullyQualifiedName~Candidate_snapshot_matrix_covers_primary_workspaces_themes_and_widths'

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

New-Item -ItemType Directory -Path $auditRoot | Out-Null

try {
    if (-not (Test-Path -LiteralPath $candidateDirectory)) {
        throw "Candidate directory does not exist. Run scripts/capture-ui-snapshot-candidates.ps1 -Capture first."
    }

    $env:NVT_UI_CANDIDATE_DIR = $candidateDirectory
    $env:NVT_UI_CANDIDATE_MODE = 'verify'
    $env:NVT_UI_AUDIT_DIR = $auditRoot
    for ($run = 1; $run -le $Runs; $run++) {
        dotnet test $project -c $Configuration --nologo --filter $testFilter "-p:BaseOutputPath=$buildOutputName\"
        if ($LASTEXITCODE -ne 0) {
            throw "Candidate verification run $run failed. Review actual/diff/metrics in '$auditRoot'."
        }
    }

    $snapshots = Get-ChildItem -LiteralPath $candidateDirectory -File -Filter '*.png'
    $totalBytes = ($snapshots | Measure-Object Length -Sum).Sum
    Write-Host "Verified local snapshot candidates: $($snapshots.Count)"
    Write-Host "Candidate bytes: $totalBytes"
    Write-Host "Exact candidate differences across $Runs run(s): 0"
}
finally {
    Remove-Item Env:NVT_UI_CANDIDATE_DIR,Env:NVT_UI_CANDIDATE_MODE,Env:NVT_UI_AUDIT_DIR -ErrorAction SilentlyContinue
    dotnet clean $project -c $Configuration --nologo "-p:BaseOutputPath=$buildOutputName\" | Out-Null
    Remove-IsolatedBuildOutputs

    if (-not $KeepAuditOutput -and (Test-Path -LiteralPath $auditRoot)) {
        $resolvedAuditRoot = [System.IO.Path]::GetFullPath($auditRoot)
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $resolvedAuditRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([System.IO.Path]::GetFileName($resolvedAuditRoot)).StartsWith('nvt-ui-candidate-', [System.StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected audit path: $resolvedAuditRoot"
        }
        Remove-Item -LiteralPath $resolvedAuditRoot -Recurse -Force
    }
}
