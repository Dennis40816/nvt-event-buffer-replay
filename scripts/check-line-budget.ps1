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
    return @(Get-ChildItem -LiteralPath $Root -Recurse -File -Force | Where-Object {
        $RelativePath = [IO.Path]::GetRelativePath($Root, $_.FullName)
        $_.Extension -in '.cs', '.axaml' -and
        $RelativePath -notmatch '(^|[\\/])(bin|obj)[\\/]'
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
$ObservedAllowlisted = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
$Problems = @()

if ($ProductionFiles.Count -eq 0) {
    $Problems += 'No production .cs or .axaml files found under src/.'
}

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
        $ObservedAllowlisted.Add($RelativePath) | Out-Null
        $AllowlistedFiles += [ordered]@{ path = $RelativePath; lines = $LineCount; ceiling = $Ceiling }
        if ($LineCount -le $PerFileLimit) {
            $OffendingFiles += [ordered]@{ path = $RelativePath; lines = $LineCount; ceiling = $Ceiling; reason = 'remove-baseline' }
            $Problems += "$RelativePath is at or below $PerFileLimit lines; remove its entry from eng/file-size-baseline.json."
        }
        elseif ($LineCount -lt $Ceiling) {
            $OffendingFiles += [ordered]@{ path = $RelativePath; lines = $LineCount; ceiling = $Ceiling; reason = 'lower-baseline' }
            $Problems += "$RelativePath shrank to $LineCount lines; lower its value in eng/file-size-baseline.json from $Ceiling to $LineCount."
        }
        elseif ($LineCount -gt $Ceiling) {
            $OffendingFiles += [ordered]@{ path = $RelativePath; lines = $LineCount; ceiling = $Ceiling; reason = 'above-baseline' }
            $Problems += "$RelativePath exceeds its $Ceiling-line baseline ceiling ($LineCount lines)."
        }
    }
    elseif ($LineCount -gt $PerFileLimit) {
        $OffendingFiles += [ordered]@{ path = $RelativePath; lines = $LineCount; ceiling = $PerFileLimit; reason = 'over-limit' }
        $Problems += "$RelativePath exceeds the $PerFileLimit-line per-file limit ($LineCount lines)."
    }
}

foreach ($Entry in $Baseline.files.GetEnumerator()) {
    if (-not $ObservedAllowlisted.Contains($Entry.Key)) {
        $OffendingFiles += [ordered]@{ path = $Entry.Key; lines = $null; ceiling = [int]$Entry.Value; reason = 'missing-baseline-file' }
        $Problems += "$($Entry.Key) is missing; remove its entry from eng/file-size-baseline.json."
    }
}

$Status = if ($Problems.Count -gt 0) { 'fail' } else { 'pass' }
$Report = [ordered]@{
    schemaVersion = '2.0'
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    production = $Production
    tests = Measure-Lines (Join-Path $RepoRoot 'tests')
    perFileLimit = $PerFileLimit
    offendingFiles = $OffendingFiles
    allowlistedFiles = $AllowlistedFiles
    projects = @($Projects.Values)
    errors = $Problems
    status = $Status
}

if ($ReportPath) {
    $FullReportPath = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($FullReportPath)) | Out-Null
    $Report | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $FullReportPath -Encoding utf8NoBOM
}
$Report | ConvertTo-Json -Depth 5
if ($Status -eq 'fail') {
    throw ($Problems -join [Environment]::NewLine)
}
