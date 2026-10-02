[CmdletBinding()]
param(
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$SrcRoot = Join-Path $RepoRoot 'src'
$PerFileLimit = 800
$BaselinePath = Join-Path $RepoRoot 'eng/file-size-baseline.json'
$Baseline = Get-Content -LiteralPath $BaselinePath -Raw | ConvertFrom-Json -AsHashtable

function Get-CountedFiles([string]$Root) {
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        $_.Extension -in '.cs', '.axaml' -and
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
    } | Sort-Object FullName)
}

function Measure-Lines([string]$Root) {
    $Files = @(Get-CountedFiles $Root)
    $Lines = 0
    foreach ($File in $Files) {
        $Lines += ([IO.File]::ReadLines($File.FullName) | Measure-Object).Count
    }
    return [ordered]@{ files = $Files.Count; lines = $Lines }
}

$ProductionFiles = @(Get-CountedFiles $SrcRoot)
$Production = [ordered]@{ files = $ProductionFiles.Count; lines = 0 }
$Projects = [ordered]@{}
$OffendingFiles = @()
$AllowlistedFiles = @()

foreach ($File in $ProductionFiles) {
    $RelativePath = [IO.Path]::GetRelativePath($RepoRoot, $File.FullName).Replace('\', '/')
    $ProjectName = $RelativePath.Split('/')[1]
    $LineCount = ([IO.File]::ReadLines($File.FullName) | Measure-Object).Count
    $Production.lines += $LineCount
    if (-not $Projects.Contains($ProjectName)) {
        $Projects[$ProjectName] = [ordered]@{ project = $ProjectName; files = 0; lines = 0 }
    }
    $Projects[$ProjectName].files++
    $Projects[$ProjectName].lines += $LineCount

    $IsAllowlisted = $Baseline.files.Contains($RelativePath)
    $Ceiling = if ($IsAllowlisted) { [int]$Baseline.files[$RelativePath] } else { $PerFileLimit }
    if ($IsAllowlisted) {
        $AllowlistedFiles += [ordered]@{ path = $RelativePath; lines = $LineCount; ceiling = $Ceiling }
        if ($LineCount -lt $Ceiling) {
            Write-Host "Note: $RelativePath has shrunk to $LineCount lines; lower its baseline ceiling from $Ceiling."
        }
    }
    if ($LineCount -gt $Ceiling) {
        $OffendingFiles += [ordered]@{ path = $RelativePath; lines = $LineCount; ceiling = $Ceiling }
    }
}

$Status = if ($OffendingFiles.Count -gt 0) { 'fail' } else { 'pass' }
$Report = [ordered]@{
    schemaVersion = '2.0'
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    production = $Production
    tests = Measure-Lines (Join-Path $RepoRoot 'tests')
    perFileLimit = $PerFileLimit
    offendingFiles = $OffendingFiles
    allowlistedFiles = $AllowlistedFiles
    projects = @($Projects.Values)
    status = $Status
}

if ($ReportPath) {
    $FullReportPath = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($FullReportPath)) | Out-Null
    $Report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $FullReportPath -Encoding utf8NoBOM
}
$Report | ConvertTo-Json -Depth 5
if ($Status -eq 'fail') {
    throw "Handwritten production file ceiling exceeded: $($OffendingFiles.Count) file(s)."
}
