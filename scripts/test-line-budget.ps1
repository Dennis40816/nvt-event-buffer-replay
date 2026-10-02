[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Checker = Join-Path $PSScriptRoot 'check-line-budget.ps1'
$TempRoot = Join-Path ([IO.Path]::GetTempPath()) ("nvt-file-size-budget-" + [Guid]::NewGuid().ToString('N'))

function New-Case([string]$Name, [hashtable]$BaselineFiles) {
    $Root = Join-Path $TempRoot $Name
    foreach ($Directory in 'src/App', 'tests', 'eng', 'scripts') {
        [IO.Directory]::CreateDirectory((Join-Path $Root $Directory)) | Out-Null
    }
    Copy-Item -LiteralPath $Checker -Destination (Join-Path $Root 'scripts/check-line-budget.ps1')
    @{ files = $BaselineFiles } | ConvertTo-Json -Depth 3 |
        Set-Content -LiteralPath (Join-Path $Root 'eng/file-size-baseline.json') -Encoding utf8NoBOM
    return $Root
}

function Add-File([string]$Root, [string]$Name, [int]$Lines) {
    [IO.File]::WriteAllText((Join-Path $Root "src/App/$Name"), ("x`n" * $Lines))
}

function Assert-Case([string]$Name, [string]$Root, [string]$ExpectedStatus, [int]$ExpectedOffenders) {
    $ReportPath = Join-Path $Root 'report.json'
    $PreviousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        & pwsh -NoProfile -File (Join-Path $Root 'scripts/check-line-budget.ps1') -ReportPath $ReportPath 2>&1 | Out-Null
        $ExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $PreviousPreference
    }
    $Report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
    $ExpectedExit = if ($ExpectedStatus -eq 'pass') { 0 } else { 1 }
    if ($ExitCode -ne $ExpectedExit -or $Report.status -ne $ExpectedStatus -or
        $Report.perFileLimit -ne 800 -or @($Report.offendingFiles).Count -ne $ExpectedOffenders) {
        throw "$Name failed: exit=$ExitCode status=$($Report.status) offenders=$(@($Report.offendingFiles).Count)."
    }
    Write-Host "PASS $Name"
}

try {
    $Root = New-Case 'new-801' @{}
    Add-File $Root 'New.cs' 801
    Assert-Case 'new 801-line file fails' $Root 'fail' 1

    $Root = New-Case 'new-800' @{}
    Add-File $Root 'New.cs' 800
    Assert-Case 'new 800-line file passes' $Root 'pass' 0

    $Root = New-Case 'allowlisted-at-ceiling' @{ 'src/App/Legacy.cs' = 900 }
    Add-File $Root 'Legacy.cs' 900
    Assert-Case 'allowlisted file at ceiling passes' $Root 'pass' 0

    $Root = New-Case 'allowlisted-over-ceiling' @{ 'src/App/Legacy.cs' = 900 }
    Add-File $Root 'Legacy.cs' 901
    Assert-Case 'allowlisted file above ceiling fails' $Root 'fail' 1

    $Root = New-Case 'large-total' @{}
    foreach ($Number in 1..41) {
        Add-File $Root "Part$Number.cs" 800
    }
    Assert-Case '32,800-line total passes' $Root 'pass' 0
}
finally {
    if (Test-Path -LiteralPath $TempRoot) {
        Remove-Item -LiteralPath $TempRoot -Recurse -Force
    }
}
