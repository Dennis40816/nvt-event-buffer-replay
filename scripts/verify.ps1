[CmdletBinding()]
param(
    [switch]$SkipPerformanceSmoke,

    [string]$ExpectedTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

& (Join-Path $PSScriptRoot 'verify-release-identity.ps1') -ExpectedTag $ExpectedTag
& python -B (Join-Path $PSScriptRoot 'fetch_core_packages.py') --manifest (Join-Path $RepoRoot 'core-packages.json') --dest (Join-Path $RepoRoot 'artifacts/core-packages')
if ($LASTEXITCODE -ne 0) { throw 'Core package download failed.' }
dotnet restore (Join-Path $RepoRoot 'Nvt.EventBufferReplay.sln') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
dotnet build (Join-Path $RepoRoot 'Nvt.EventBufferReplay.sln') --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
dotnet test (Join-Path $RepoRoot 'Nvt.EventBufferReplay.sln') --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& pwsh -NoProfile -File (Join-Path $RepoRoot 'tools/repo-checks/repo-health.ps1') -Mode Verify -Repo nfu -Root $RepoRoot
if ($LASTEXITCODE -ne 0) { throw 'Syntax health ratchet failed.' }
& pwsh -NoProfile -File (Join-Path $PSScriptRoot 'measure-test-duplication.ps1') -Root $RepoRoot -Verify
if ($LASTEXITCODE -ne 0) { throw 'Test duplication ratchet failed.' }
& (Join-Path $PSScriptRoot 'check-line-budget.ps1')
& (Join-Path $PSScriptRoot 'test-line-budget.ps1')
if (-not $SkipPerformanceSmoke) { & (Join-Path $PSScriptRoot 'performance-gate.ps1') -Mode Smoke }

Write-Output 'Repository verification passed.'
