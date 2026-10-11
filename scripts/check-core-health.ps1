# Copyright (c) 2026 Dennis Liu. All rights reserved.
#Requires -Version 7.0
[CmdletBinding()]
param([string]$Root = '.', [string]$BaseRef = '', [string]$CoreRoot = '')

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
Push-Location -LiteralPath $Root
try {
    . (Join-Path $Root 'tools/repo-checks/repo-health.ps1') -FunctionsOnly -Repo nfu -Root $Root -BaseRef $BaseRef -CoreRoot $CoreRoot
    $script:GitRoot = [IO.Path]::GetFullPath((Invoke-Checked 'git' @('rev-parse', '--show-toplevel')).Trim())
    Test-BundleDrift
    Write-Output 'Core health bundle drift check passed.'
}
finally { Pop-Location }
