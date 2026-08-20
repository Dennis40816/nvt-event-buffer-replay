[CmdletBinding()]
param(
    [switch]$SkipPerformanceSmoke,

    [string]$ExpectedTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

& (Join-Path $PSScriptRoot 'verify-release-identity.ps1') -ExpectedTag $ExpectedTag
dotnet restore (Join-Path $RepoRoot 'Nvt.EventBufferReplay.sln') --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
dotnet build (Join-Path $RepoRoot 'Nvt.EventBufferReplay.sln') --configuration Release --no-restore
if ($LASTEXITCODE -ne 0) { throw 'Release build failed.' }
dotnet test (Join-Path $RepoRoot 'Nvt.EventBufferReplay.sln') --configuration Release --no-build
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& (Join-Path $PSScriptRoot 'check-line-budget.ps1')
if (-not $SkipPerformanceSmoke) { & (Join-Path $PSScriptRoot 'performance-gate.ps1') -Mode Smoke }

Write-Output 'Repository verification passed.'
