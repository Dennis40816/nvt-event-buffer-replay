# Copyright (c) 2026 Dennis Liu. All rights reserved.
#Requires -Version 7.0
[CmdletBinding()]
param([string]$Root, [string]$HealthScript)

# Offline fixture: use the same readers, split and comparisons as Verify.
# Measure ran separately; this does not exercise build, SARIF or format providers.
. $HealthScript -FunctionsOnly -Repo nfu -Root $Root
$script:Failures = 0
$script:ChangedPaths = @()
try {
    $baseline = Read-Baseline ([IO.File]::ReadAllText((Join-Path $Root $BaselinePath))) $BaselinePath
    $debt = Read-TestDebt ([IO.File]::ReadAllText((Join-Path $Root $TestDebtPath))) $TestDebtPath
    $measurement = ConvertFrom-Json -AsHashtable -Depth 100 ([IO.File]::ReadAllText((Join-Path $Root 'measurement.json')))
    Split-TestFindings $measurement
    Compare-Debt $baseline $measurement 'values' $true
    Compare-TestDebtMeasurement $debt $measurement $true
    if ($script:Failures) { exit 1 }
    Write-Output 'Fixture Verify comparisons passed.'
    exit 0
}
catch {
    Write-Output "Fixture input error: $($_.Exception.GetBaseException().Message)"
    exit 2
}
