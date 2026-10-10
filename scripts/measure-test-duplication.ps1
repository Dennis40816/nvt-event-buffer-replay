# Copyright (c) 2026 Dennis Liu. All rights reserved.
#Requires -Version 7.0
[CmdletBinding()]
param([string]$Root = '.', [string]$OutputPath = 'eng/code-health/test-duplication.json', [switch]$Verify)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)
$patterns = [ordered]@{
    tempPaths = 'Path\s*\.\s*GetTempPath\s*\(|\bGetTempFileName\s*\('
    workspaceClasses = '\bclass\s+\w*(?:Workspace|TempDir)\w*\b(?!Tests\b|TestHelpers\b)'
    fakeClocks = '\bclass\s+(?:\w*(?:Fake|Manual|Test)\w*(?:Clock|TimeProvider)\w*|ClockState)\b|:\s*TimeProvider\b'
    realTimeWaits = '\b(?:Task\s*\.\s*Delay|Thread\s*\.\s*Sleep)\s*\('
    elapsedTime = '\bStopwatch\b|\.Elapsed\w*\b'
    headlessSetup = '\bUseHeadless\s*\(|\bAvaloniaHeadless\w*\b'
    childProcesses = '\bProcess\s*\.\s*Start\s*\(|\bProcessStartInfo\b'
    repoRootFinders = '\b(?:string|DirectoryInfo)\s+(?:Find|Get|Locate|Resolve)(?:Repo|Repository|Solution)Root\s*\('
    sourceTextReads = 'ReadAllText(?:Async)?\s*\([^;]*["''][^"'']+\.(?:cs|axaml)["'']|\bReadSourceText\s*\('
}
$metrics = [ordered]@{}
foreach ($name in $patterns.Keys) { $metrics[$name] = [ordered]@{ count = 0; files = @() } }
$fakeNames = @{}
$files = @(Get-ChildItem (Join-Path $Root 'tests') -Recurse -Filter '*.cs' -File |
    Where-Object { $_.FullName -notmatch '[/\\](?:bin|obj|Fixtures)[/\\]' } | Sort-Object FullName)
foreach ($file in $files) {
    $path = [IO.Path]::GetRelativePath($Root, $file.FullName).Replace('\', '/')
    $text = [IO.File]::ReadAllText($file.FullName)
    foreach ($name in $patterns.Keys) {
        # Workspace test classes are consumers, not handmade workspace helpers.
        $matches = [regex]::Matches($text, $patterns[$name])
        if ($name -eq 'workspaceClasses') { $matches = @($matches | Where-Object { $_.Value -notmatch '(?:Tests|TestHelpers)$' }) }
        if ($matches.Count) { $metrics[$name].files += $path; $metrics[$name].count++ }
    }
    foreach ($match in [regex]::Matches($text, '\bclass\s+((?:Fake|Stub|Mock|Recording)\w+)\b')) {
        $name = $match.Groups[1].Value
        if (-not $fakeNames.ContainsKey($name)) { $fakeNames[$name] = @() }
        $fakeNames[$name] += $path
    }
}
$duplicates = [ordered]@{}
foreach ($name in @($fakeNames.Keys | Sort-Object)) {
    $paths = @($fakeNames[$name] | Sort-Object -Unique)
    if ($paths.Count -ge 2) { $duplicates[$name] = [ordered]@{ count = $paths.Count; files = $paths } }
}
$result = [ordered]@{ schemaVersion = 1; counting = 'Matching C# files, not call sites. Includes shared helpers. Excludes bin, obj and Fixtures. Workspace classes exclude Tests and TestHelpers consumers. Source reads match literal source paths or the shared ReadSourceText seam.'; testFiles = $files.Count; metrics = $metrics; duplicateFakeClasses = $duplicates }
$destination = [IO.Path]::GetFullPath($OutputPath, $Root)
if ($Verify) {
    $baseline = Get-Content -LiteralPath $destination -Raw | ConvertFrom-Json -AsHashtable
    $failures = 0
    foreach ($group in @('metrics', 'duplicateFakeClasses')) {
        foreach ($name in $result[$group].Keys) {
            $old = if ($baseline[$group].Contains($name)) { $baseline[$group][$name].count } else { 0 }
            $now = $result[$group][$name].count
            if ($now -gt $old) { Write-Output "TEST_DUPLICATION $name increased: $old -> $now"; $failures++ }
        }
        foreach ($name in $baseline[$group].Keys) {
            $now = if ($result[$group].Contains($name)) { $result[$group][$name].count } else { 0 }
            if ($now -lt $baseline[$group][$name].count) { Write-Output "TEST_DUPLICATION $name fell: $($baseline[$group][$name].count) -> $now. Lower the baseline." }
        }
    }
    if ($failures) { exit 1 }
    Write-Output 'Test duplication ceilings passed.'
} else {
    [IO.Directory]::CreateDirectory((Split-Path $destination -Parent)) | Out-Null
    [IO.File]::WriteAllText($destination, ($result | ConvertTo-Json -Depth 20) + "`n")
    $metrics.GetEnumerator() | ForEach-Object { Write-Output "$($_.Key): $($_.Value.count)" }
    $duplicates.GetEnumerator() | ForEach-Object { Write-Output "$($_.Key): $($_.Value.count) definitions in distinct files" }
}
