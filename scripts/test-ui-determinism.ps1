[CmdletBinding()]
param(
    [ValidateRange(2, 10)]
    [int]$Runs = 2,
    [ValidateSet('Debug', 'Release')]
    [string]$Configuration = 'Release',
    [switch]$KeepAuditOutput
)

$ErrorActionPreference = 'Stop'
$repoRoot = [System.IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$project = Join-Path $repoRoot 'tests\Nvt.Replay.Avalonia.Tests\Nvt.Replay.Avalonia.Tests.csproj'
$auditRoot = Join-Path ([System.IO.Path]::GetTempPath()) ('nvt-ui-determinism-' + [Guid]::NewGuid().ToString('N'))
$buildOutputName = 'bin-ui-determinism'

New-Item -ItemType Directory -Path $auditRoot | Out-Null

try {
    $runManifests = @()
    for ($run = 1; $run -le $Runs; $run++) {
        $runDirectory = Join-Path $auditRoot ('run-{0:D2}' -f $run)
        New-Item -ItemType Directory -Path $runDirectory | Out-Null
        $env:NVT_UI_AUDIT_DIR = $runDirectory

        dotnet test $project -c $Configuration --nologo "-p:BaseOutputPath=$buildOutputName\"
        if ($LASTEXITCODE -ne 0) {
            throw "UI test run $run failed with exit code $LASTEXITCODE."
        }

        $manifest = Get-ChildItem -LiteralPath $runDirectory -File |
            Sort-Object Name |
            ForEach-Object {
                [pscustomobject]@{
                    Name = $_.Name
                    Hash = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash
                    Bytes = $_.Length
                }
            }
        $runManifests += ,$manifest
    }

    $baseline = $runManifests[0]
    $differences = @()
    for ($run = 1; $run -lt $runManifests.Count; $run++) {
        $differences += Compare-Object $baseline $runManifests[$run] -Property Name, Hash
    }

    $totalBytes = ($baseline | Measure-Object Bytes -Sum).Sum
    Write-Host "Snapshots: $($baseline.Count)"
    Write-Host "Baseline bytes: $totalBytes"
    Write-Host "Repeated-run hash differences: $($differences.Count)"

    if ($differences.Count -gt 0) {
        $differences | Format-Table Name, Hash, SideIndicator -AutoSize
        throw 'UI screenshots are not deterministic. Review the differing states before approving baselines.'
    }
}
finally {
    Remove-Item Env:NVT_UI_AUDIT_DIR -ErrorAction SilentlyContinue
    dotnet clean $project -c $Configuration --nologo "-p:BaseOutputPath=$buildOutputName\" | Out-Null

    if (-not $KeepAuditOutput -and (Test-Path -LiteralPath $auditRoot)) {
        $resolvedAuditRoot = [System.IO.Path]::GetFullPath($auditRoot)
        $tempRoot = [System.IO.Path]::GetFullPath([System.IO.Path]::GetTempPath())
        if (-not $resolvedAuditRoot.StartsWith($tempRoot, [System.StringComparison]::OrdinalIgnoreCase) -or
            -not ([System.IO.Path]::GetFileName($resolvedAuditRoot)).StartsWith('nvt-ui-determinism-', [System.StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected audit path: $resolvedAuditRoot"
        }
        Remove-Item -LiteralPath $resolvedAuditRoot -Recurse -Force
    }
}
