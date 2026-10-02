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
    $Path = Join-Path $Root "src/App/$Name"
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path)) | Out-Null
    [IO.File]::WriteAllText($Path, ("x`n" * $Lines))
}

function Assert-Case(
    [string]$Name, [string]$Root, [string]$ExpectedStatus, [int]$ExpectedOffenders,
    [bool]$ExpectReport = $true, [string]$ExpectedMessage = ''
) {
    $ReportPath = Join-Path $Root 'report.json'
    $PreviousPreference = $ErrorActionPreference
    try {
        $ErrorActionPreference = 'Continue'
        $RunOutput = & pwsh -NoProfile -File (Join-Path $Root 'scripts/check-line-budget.ps1') -ReportPath $ReportPath 2>&1 | Out-String
        $ExitCode = $LASTEXITCODE
    }
    finally {
        $ErrorActionPreference = $PreviousPreference
    }
    $ExpectedExit = if ($ExpectedStatus -eq 'pass') { 0 } else { 1 }
    if ($ExitCode -ne $ExpectedExit -or (Test-Path -LiteralPath $ReportPath) -ne $ExpectReport -or
        ($ExpectedMessage -and -not $RunOutput.Contains($ExpectedMessage))) {
        throw "$Name failed: exit=$ExitCode output=$RunOutput"
    }
    if ($ExpectReport) {
        $Report = Get-Content -LiteralPath $ReportPath -Raw | ConvertFrom-Json
        if ($Report.status -ne $ExpectedStatus -or $Report.perFileLimit -ne 800 -or
            @($Report.offendingFiles).Count -ne $ExpectedOffenders) {
            throw "$Name failed: status=$($Report.status) offenders=$(@($Report.offendingFiles).Count)."
        }
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

    $Root = New-Case 'axaml-801' @{}
    Add-File $Root 'View.axaml' 801
    Assert-Case '801-line axaml file fails' $Root 'fail' 1

    $Root = New-Case 'source-bin-only' @{}
    Add-File $Root 'bin/Hidden.cs' 801
    Assert-Case 'source bin folder leaves no production files and fails' $Root 'fail' 0 $true 'No production'

    $Root = New-Case 'tracked-bin' @{}
    Add-File $Root 'New.cs' 1
    Add-File $Root 'bin/Hidden.cs' 801
    Set-Content -LiteralPath (Join-Path $Root '.gitignore') -Value "bin/`nobj/" -Encoding utf8NoBOM
    $GitConfig = @('-c', 'user.name=Line Budget Test', '-c', 'user.email=line-budget@example.invalid', '-c', 'core.autocrlf=false')
    & git @GitConfig -C $Root init -q
    if ($LASTEXITCODE -ne 0) { throw 'Could not initialize temporary git repository.' }
    & git @GitConfig -C $Root add -f -- src/App/bin/Hidden.cs
    if ($LASTEXITCODE -ne 0) { throw 'Could not force-add hidden source file.' }
    Assert-Case 'tracked source bin file fails' $Root 'fail' 1 $true 'src/App/bin/Hidden.cs'
    & git @GitConfig -C $Root rm -q --cached -- src/App/bin/Hidden.cs
    if ($LASTEXITCODE -ne 0) { throw 'Could not untrack hidden source file.' }
    Assert-Case 'untracked source bin file passes' $Root 'pass' 0

    $Root = New-Case 'bin/repository-root' @{}
    Add-File $Root 'New.cs' 801
    Assert-Case 'repository path containing bin still counts files' $Root 'fail' 1

    $Root = New-Case 'empty-src' @{}
    Assert-Case 'empty src fails' $Root 'fail' 0 $true 'No production'

    $Root = New-Case 'missing-baseline-json' @{}
    Add-File $Root 'New.cs' 800
    Remove-Item -LiteralPath (Join-Path $Root 'eng/file-size-baseline.json')
    Assert-Case 'missing baseline JSON fails' $Root 'fail' 0 $false 'file-size-baseline.json'

    $Root = New-Case 'allowlisted-at-ceiling' @{ 'src/App/Legacy.cs' = 900 }
    Add-File $Root 'Legacy.cs' 900
    Assert-Case 'allowlisted file at ceiling passes' $Root 'pass' 0

    $Root = New-Case 'allowlisted-over-ceiling' @{ 'src/App/Legacy.cs' = 900 }
    Add-File $Root 'Legacy.cs' 901
    Assert-Case 'allowlisted file above ceiling fails' $Root 'fail' 1

    $Root = New-Case 'allowlisted-shrunk' @{ 'src/App/Legacy.cs' = 900 }
    Add-File $Root 'Legacy.cs' 899
    Assert-Case 'shrunk allowlisted file requires lower baseline' $Root 'fail' 1 $true 'lower its value in eng/file-size-baseline.json'

    $Root = New-Case 'allowlisted-at-limit' @{ 'src/App/Legacy.cs' = 900 }
    Add-File $Root 'Legacy.cs' 800
    Assert-Case 'allowlisted file at 800 requires entry removal' $Root 'fail' 1 $true 'remove its entry from eng/file-size-baseline.json'

    $Root = New-Case 'missing-allowlisted-file' @{ 'src/App/Absent.cs' = 900 }
    Add-File $Root 'Existing.cs' 1
    Assert-Case 'missing allowlisted file requires entry removal' $Root 'fail' 1 $true 'remove its entry from eng/file-size-baseline.json'

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
