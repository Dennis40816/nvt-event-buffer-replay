[CmdletBinding()]
param(
    [string]$ReportPath
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))

function Measure-Lines([string]$Root) {
    $Files = @(Get-ChildItem -LiteralPath $Root -Recurse -File | Where-Object {
        $_.Extension -in '.cs', '.axaml' -and
        $_.FullName -notmatch '[\\/](bin|obj)[\\/]'
    })
    $Lines = 0
    foreach ($File in $Files) {
        $Lines += ([IO.File]::ReadLines($File.FullName) | Measure-Object).Count
    }
    return [ordered]@{ files = $Files.Count; lines = $Lines }
}

$Production = Measure-Lines (Join-Path $RepoRoot 'src')
$Tests = Measure-Lines (Join-Path $RepoRoot 'tests')
$Status = if ($Production.lines -gt 30000) { 'fail' } elseif ($Production.lines -gt 25000) { 'review-required' } else { 'pass' }
$Report = [ordered]@{
    schemaVersion = '1.0'
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    production = $Production
    tests = $Tests
    target = '18000-22000'
    architectureReviewAt = 25000
    hardCap = 30000
    status = $Status
}

if ($ReportPath) {
    $FullReportPath = [IO.Path]::GetFullPath($ReportPath)
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($FullReportPath)) | Out-Null
    $Report | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath $FullReportPath -Encoding utf8NoBOM
}
$Report | ConvertTo-Json -Depth 4
if ($Status -ne 'pass') { throw "Handwritten production line budget status: $Status ($($Production.lines) lines)." }
