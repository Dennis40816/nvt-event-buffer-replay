# Copyright (c) 2026 Dennis Liu. All rights reserved.
#Requires -Version 7.0
<#
C# health enforcement; syntax measurement contract roslyn-physical-v1. No package restore.
The embedded host uses the global.json-selected SDK's Roslyn, in an isolated ALC.
Narrow discovery choices (also in repo-health.md): unresolved async lambdas remain
candidates; blockingWait is syntax only; viewTypeLines uses the 800-line budget, also for
a code-only Avalonia view above it;
clock candidates are TimeProvider descendants or Fake/Manual/Test*Clock,
Fake/Manual/Test*TimeProvider, ClockState; workspace lifecycle candidates contain
both Directory.CreateDirectory and Directory.Delete with a Path.GetTempPath call.
Source-read discovery uses ReadText/ReadAllText[Async]/ReadAllLines[Async]/
ReadAllBytes[Async]/OpenText and Contains/DoesNotContain/Matches/DoesNotMatch in
the same file. Discovery is retained everywhere, with a separate plan-filter flag.
The shared *.TestSupport project is the one reviewed owner of a fake clock, a temporary workspace and a blocking
wait (conventions T2). It is exempt from fakeClockDuplicates, workspaceDuplicates and blockingWait.
*.TestSupport.Tests and every other project are not exempt.
Build outputs (bin, obj, artifacts, .git) are excluded; generated filename suffixes
are NOT excluded. State includes init and positional-record properties; observable
backing fields are counted once, partial properties by symbol once.
H03 validates schema, evaluated project coverage, build/SARIF/format and provenance.
Measure is syntax-only. Verify, LowerBaseline and Enroll require a solution.
Test-duplication rules (timeWaitsInTests, elapsedAssertionsInTests, tempPathInTests,
sourceTextReadsInTests, headlessSessionSetups, childProcessInTests, sameNameFakes) have their own
ledger, eng/code-health/test-debt.json. EnrollTestDebt (syntax only) creates it once; after that
the ledger can only shrink. The shared Nvt.Core.TestSupport project is exempt.
Pass -Solution to EnrollTestDebt so that linked files are attributed as Verify does.
#>
[CmdletBinding()]
param(
    [string]$Mode = 'Verify',
    [string]$Repo = 'core',
    [string]$Root = '.',
    [string]$BaseRef = '',
    [string]$Solution = '',
    [string]$CoreRoot = '',
    [string]$Owner = '',
    [switch]$FunctionsOnly,
    [string]$BaselinePath = 'eng/code-health/baseline.json',
    [string]$TestDebtPath = 'eng/code-health/test-debt.json',
    [Alias('OutFile')][string]$OutputPath = '',
    # A diagnostic hook: cannot substitute another parser for the pinned one.
    [string]$ParserDirectory = ''
)
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
# Stable output on every host: UTF-8 without BOM, and compiler messages in the invariant culture.
[Console]::OutputEncoding = [Text.UTF8Encoding]::new($false)
[Globalization.CultureInfo]::CurrentUICulture = [Globalization.CultureInfo]::InvariantCulture
# Fixed tool language on every host: localized compiler and format messages change the fingerprints.
$env:DOTNET_CLI_UI_LANGUAGE = 'en'
$env:VSLANG = '1033'
$env:PreferredUILang = 'en-US'
# Host limits: at most four build nodes, and no MSBuild node left running after a step (dotnet format included).
$env:MSBUILDDISABLENODEREUSE = '1'
$script:MsbuildLimits = @('-m:4', '-nodeReuse:false')
$script:Version = 'roslyn-physical-v1'
$script:Limits = @{ fileLines = 800; methodLines = 80; partialFiles = 8; stateMembers = 30; axamlCodeBehindLines = 150; viewTypeLines = 800 }
# Test-duplication rules keep their own ledger (eng/code-health/test-debt.json), apart from the main baseline.
$script:TestRules = @('timeWaitsInTests', 'elapsedAssertionsInTests', 'tempPathInTests', 'sourceTextReadsInTests', 'headlessSessionSetups', 'childProcessInTests', 'sameNameFakes')
$script:SyntaxRules = @('asyncVoid', 'blockingWait', 'suppressions', 'generationFields', 'nativeImportDuplicates', 'fakeClockDuplicates', 'workspaceDuplicates', 'suppressionScopes') + $script:TestRules

function Invoke-Checked([string]$Exe, [string[]]$Arguments) {
    $resultFile = ''
    if ($Exe -eq 'dotnet' -and $Arguments.Count -gt 0 -and $Arguments[0] -in @('build', 'msbuild')) {
        $Arguments = @($Arguments) + $script:MsbuildLimits
        # A -get* result goes to a file, so warnings that a target prints on a fresh tree cannot mix into the JSON.
        if ($Arguments[0] -eq 'msbuild' -and @($Arguments | Where-Object { $_ -like '-get*' }).Count) {
            $resultFile = Join-Path ([IO.Path]::GetTempPath()) "health-msbuild-$([guid]::NewGuid().ToString('N')).json"
            $Arguments = @($Arguments) + "-getResultOutputFile:$resultFile"
        }
    }
    try {
        $lines = @(& $Exe @Arguments 2>&1)
        if ($LASTEXITCODE -ne 0) { throw "$Exe exited $LASTEXITCODE`: $($lines -join [Environment]::NewLine)" }
        if ($resultFile) { return (Get-Content -LiteralPath $resultFile -Raw) }
        return ($lines -join [Environment]::NewLine)
    }
    finally {
        if ($resultFile -and (Test-Path -LiteralPath $resultFile)) { Remove-Item -LiteralPath $resultFile -Force }
    }
}

function Read-Baseline([string]$Text, [string]$Label) {
    # Schema plus semantic ratchet checks; no package/module dependency.
    try { $b = ConvertFrom-Json -InputObject $Text -AsHashtable -Depth 100 }
    catch { throw "Bad baseline $Label`: invalid JSON: $($_.Exception.Message)" }
    if ($b -isnot [System.Collections.IDictionary]) { throw "Bad baseline $Label`: expected object" }
    foreach ($key in @('schemaVersion', 'measurementVersion', 'snapshotCommit', 'limits', 'entities', 'findings')) {
        if (-not $b.Contains($key)) { throw "Bad baseline $Label`: missing $key" }
    }
    if (-not (Test-Count $b.schemaVersion) -or $b.schemaVersion -ne 1 -or $b.measurementVersion -cne $script:Version) { throw "Bad baseline $Label`: schema/measurement migration required" }
    if ($b.snapshotCommit -isnot [string] -or $b.snapshotCommit -notmatch '^[0-9a-fA-F]{7,40}$') { throw "Bad baseline $Label`: invalid snapshotCommit" }
    if ($b.limits -isnot [System.Collections.IDictionary] -or $b.entities -isnot [array] -or $b.findings -isnot [array]) { throw "Bad baseline $Label`: limits/entities/findings have wrong types" }
    foreach ($metric in $script:Limits.Keys) {
        if (-not $b.limits.Contains($metric)) {
            if ($metric -eq 'viewTypeLines') { continue }
            throw "Bad baseline $Label`: missing limit $metric"
        }
    }
    foreach ($metric in $b.limits.Keys) {
        if (-not $script:Limits.ContainsKey($metric) -or -not (Test-Count $b.limits[$metric]) -or $b.limits[$metric] -gt $script:Limits[$metric]) { throw "Bad baseline $Label`: invalid/relaxed limit $metric" }
    }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($e in $b.entities) {
        foreach ($key in @('project', 'kind', 'symbol', 'locations', 'ceilings', 'owner', 'issue')) {
            if ($e -isnot [System.Collections.IDictionary] -or -not $e.Contains($key)) { throw "Bad baseline $Label`: entity missing $key" }
        }
        foreach ($key in @('project', 'kind', 'symbol', 'owner', 'issue')) { if ($e[$key] -isnot [string] -or -not $e[$key].Trim()) { throw "Bad baseline $Label`: invalid entity $key" } }
        if ($e.kind -notin @('file', 'type', 'member') -or $e.locations -isnot [array] -or $e.locations.Count -eq 0 -or $e.ceilings -isnot [System.Collections.IDictionary] -or $e.ceilings.Count -eq 0) { throw "Bad baseline $Label`: invalid entity shape" }
        foreach ($path in $e.locations) { if ($path -isnot [string] -or -not $path.Trim()) { throw "Bad baseline $Label`: invalid location" } }
        if ($e.Contains('contentHash') -and ($e.contentHash -isnot [string] -or $e.contentHash -notmatch '^[0-9a-f]{64}$')) { throw "Bad baseline $Label`: invalid contentHash" }
        if (-not $seen.Add((Entity-Key $e) + '|' + ($e.locations -join '|'))) { throw "Bad baseline $Label`: duplicate entity" }
        foreach ($metric in $e.ceilings.Keys) {
            if (-not $script:Limits.ContainsKey($metric) -or -not (Test-Count $e.ceilings[$metric]) -or $e.ceilings[$metric] -le (Get-Limit $b $metric)) { throw "Bad baseline $Label`: ceiling $metric must describe excess above its limit" }
        }
    }
    $seen.Clear()
    foreach ($f in $b.findings) {
        foreach ($key in @('rule', 'project', 'path', 'member', 'symbol', 'syntaxHash', 'count', 'owner')) {
            if ($f -isnot [System.Collections.IDictionary] -or -not $f.Contains($key)) { throw "Bad baseline $Label`: finding missing $key" }
            if ($key -ne 'count' -and ($f[$key] -isnot [string] -or -not $f[$key].Trim())) { throw "Bad baseline $Label`: invalid finding $key" }
        }
        if (-not (Test-Count $f.count) -or $f.count -eq 0 -or $f.syntaxHash -notmatch '^[0-9a-f]{64}$') { throw "Bad baseline $Label`: invalid finding count/hash/date" }
        # removeBy is optional. A finding without it has no due date. A value that is present must be a real calendar date.
        if ($f.Contains('removeBy') -and -not (Test-IsoDate $f.removeBy)) { throw "Bad baseline $Label`: invalid finding count/hash/date" }
        if (-not $seen.Add((Finding-Key $f))) { throw "Bad baseline $Label`: duplicate finding fingerprint" }
        # Diagnostic fingerprints, including bannedApi/RS0030, use exactly this
        # reader/key. H03 must populate them before enabling complete Verify.
    }
    [void](Read-HealthDocument $Text 'baseline')
    return $b
}
function Test-Count($Value) { return (($Value -is [int] -or $Value -is [long]) -and $Value -ge 0) }
function Test-IsoDate($Value) {
    $parsed = [DateTime]::MinValue
    return $Value -is [string] -and [DateTime]::TryParseExact($Value, 'yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture, [Globalization.DateTimeStyles]::None, [ref]$parsed)
}
# The one clock of the removeBy check. Tests replace this function to set the date.
function Get-HealthToday { return [DateTime]::UtcNow.Date }
# A finding is due on its removeBy day. It fails from the next day. ISO dates sort as text.
# A finding without removeBy has no due date and never expires.
function Test-RemoveByExpiry($Ledger, [string]$LedgerPath) {
    $today = (Get-HealthToday).ToString('yyyy-MM-dd', [Globalization.CultureInfo]::InvariantCulture)
    $expired = @($Ledger.findings | Where-Object { $_.Contains('removeBy') -and [string]::CompareOrdinal($_.removeBy, $today) -lt 0 })
    # List the first 20, then one summary line, so a ledger with thousands of due findings stays readable.
    $listed = [Math]::Min($expired.Count, 20)
    for ($i = 0; $i -lt $listed; $i++) {
        Write-Failure 'HC_STATE' $expired[$i] 'removeBy' $expired[$i].removeBy $today "The removeBy date in $LedgerPath has passed. Fix the finding, or move the date in a dedicated pull request with an owner decision."
    }
    if ($expired.Count -gt $listed) {
        Write-Output "HC_STATE ${LedgerPath}:1 removeBy: $($expired.Count - $listed) more findings of this ledger have a removeBy date before $today; $listed are listed above."
        $script:Failures++
    }
}
function Get-Limit($Baseline, [string]$Metric) {
    if ($Baseline.limits.Contains($Metric)) { return $Baseline.limits[$Metric] }
    return $script:Limits[$Metric]
}
function Entity-Key($e) { return "$($e.project)|$($e.kind)|$($e.symbol)" }
function Finding-Key($f) { return "$($f.rule)|$($f.project)|$($f.member)|$($f.symbol)|$($f.syntaxHash)" }

function Match-Entities($Old, $New) {
    # Reserve strong matches before using unique symbols. A small partial file
    # must not consume the allowance belonging to a renamed oversized partial.
    $matches = @{}
    $used = [System.Collections.Generic.HashSet[int]]::new()
    foreach ($pass in @('location', 'hash', 'symbol')) {
        for ($i = 0; $i -lt $New.Count; $i++) {
            if ($matches.ContainsKey($i)) { continue }
            $n = $New[$i]
            $candidates = @(for ($j = 0; $j -lt $Old.Count; $j++) {
                if ($used.Contains($j) -or $Old[$j].project -cne $n.project -or $Old[$j].kind -cne $n.kind) { continue }
                $sameSymbol = (Entity-Key $Old[$j]) -ceq (Entity-Key $n)
                if ($pass -eq 'location' -and $sameSymbol -and @($Old[$j].locations | Where-Object { $_ -cin $n.locations }).Count -gt 0) { $j }
                if ($pass -eq 'hash' -and $Old[$j].Contains('contentHash') -and $n.Contains('contentHash') -and $Old[$j].contentHash -ceq $n.contentHash) { $j }
                if ($pass -eq 'symbol' -and $sameSymbol) { $j }
            })
            if ($candidates.Count -eq 0) { continue }
            if ($pass -eq 'symbol') {
                $remaining = @(for ($k=0; $k -lt $New.Count; $k++) { if (-not $matches.ContainsKey($k) -and (Entity-Key $New[$k]) -ceq (Entity-Key $n)) { $k } })
                if ($remaining.Count -ne 1) { continue }
            }
            if ($candidates.Count -eq 1) { $matches[$i] = $candidates[0]; [void]$used.Add($candidates[0]) }
        }
    }
    return $matches
}
function Write-Failure([string]$Code, $Entity, [string]$Metric, $Old, $New, [string]$Reason) {
    $path = if ($Entity.Contains('path')) { $Entity.path } else { $Entity.locations[0] }
    $line = if ($Entity.Contains('line')) { $Entity.line } else { 1 }
    $contributor = $path
    if ($Entity.Contains('locations')) {
        $changed = @($Entity.locations | Where-Object { $_ -cin $script:ChangedPaths })
        if ($changed.Count -gt 0) { $contributor = $changed[0] }
    }
    $contributingLine = $line
    if ($Entity.Contains('locationLines') -and $Entity.locationLines.Contains($contributor)) { $contributingLine = $Entity.locationLines[$contributor] }
    if ($Metric -eq 'suppressions' -and $Entity.Contains('member') -and $Entity.member -eq 'MSBuild') {
        $changedProps = @($script:ChangedPaths | Where-Object { $_.EndsWith('.props', [StringComparison]::OrdinalIgnoreCase) })
        if ($changedProps.Count) { $contributor = $changedProps[0]; $contributingLine = 1 }
    }
    Write-Output "$Code ${path}:${line} $Metric`: $Old -> $New; entity $($Entity.project):$($Entity.symbol); member $(if ($Entity.Contains('member')) { $Entity.member } else { $Entity.symbol }); $Reason"
    Write-Output "Contributing change: ${contributor}:${contributingLine}."
    $script:Failures++
}
function Compare-Debt($Allowed, $Current, [string]$ValueField, [bool]$RequireLower) {
    foreach ($metric in $(if ($ValueField -eq 'ceilings') { $script:Limits.Keys } else { @() })) {
        # Compare effective limits: omitting an optional limit restores its default, which can raise it.
        $oldLimit = Get-Limit $Allowed $metric
        $newLimit = Get-Limit $Current $metric
        if ($newLimit -gt $oldLimit) {
            Write-Failure 'HC_STATE' @{ locations = @($BaselinePath); project = $Repo; symbol = 'limits' } $metric $oldLimit $newLimit 'Raised limit is forbidden.'
        }
    }
    $matches = Match-Entities $Allowed.entities $Current.entities
    for ($i = 0; $i -lt $Current.entities.Count; $i++) {
        $e = $Current.entities[$i]
        foreach ($metric in $e[$ValueField].Keys) {
            $limit = Get-Limit $Allowed $metric
            $old = $limit
            if ($matches.ContainsKey($i) -and $Allowed.entities[$matches[$i]].ceilings.Contains($metric)) { $old = $Allowed.entities[$matches[$i]].ceilings[$metric] }
            $value = $e[$ValueField][$metric]
            # Compare excess, so compliant new entities do not consume debt.
            if ([Math]::Max(0, $value - $limit) -gt [Math]::Max(0, $old - $limit)) {
                Write-Failure 'HC_STATE' $e $metric $old $value 'New/growing excess or raised ceiling is forbidden.'
            }
        }
    }
    $currentFindings = [Collections.Generic.Dictionary[string,hashtable]]::new([StringComparer]::Ordinal)
    foreach ($f in $Current.findings) { $currentFindings[(Finding-Key $f)] = $f }
    $oldFindings = [Collections.Generic.Dictionary[string,hashtable]]::new([StringComparer]::Ordinal)
    foreach ($f in $Allowed.findings) { $oldFindings[(Finding-Key $f)] = $f }
    foreach ($f in $Current.findings) {
        $key = Finding-Key $f
        $old = if ($oldFindings.ContainsKey($key)) { $oldFindings[$key].count } else { 0 }
        if ($f.count -gt $old) { Write-Failure 'HC_DIAGNOSTIC' $f $f.rule $old $f.count 'New fingerprint; deleting another finding cannot offset it.' }
    }
    if ($RequireLower) {
        foreach ($old in $Allowed.entities) {
            $idx = @($matches.Keys | Where-Object { $Allowed.entities[$matches[$_]] -eq $old })
            foreach ($metric in $old.ceilings.Keys) {
                $value = if ($idx.Count -eq 1 -and $Current.entities[$idx[0]][$ValueField].Contains($metric)) { $Current.entities[$idx[0]][$ValueField][$metric] } else { 0 }
                if ($value -lt $old.ceilings[$metric]) { Write-Failure 'HC_STATE' $old $metric $old.ceilings[$metric] $value 'Fixes must lower/delete baseline debt in this change; run LowerBaseline.' }
            }
        }
        foreach ($old in $Allowed.findings) {
            $key = Finding-Key $old
            $value = if ($currentFindings.ContainsKey($key)) { $currentFindings[$key].count } else { 0 }
            if ($value -lt $old.count) { Write-Failure 'HC_DIAGNOSTIC' $old $old.rule $old.count $value 'Fixes must lower/delete baseline debt in this change; run LowerBaseline.' }
        }
    }
}

function Read-HealthDocument([string]$Text, [string]$Definition) {
    $schemaPath = Join-Path $PSScriptRoot 'csharp/schema.json'
    if (-not (Test-Path -LiteralPath $schemaPath)) { $schemaPath = Join-Path $PSScriptRoot 'schema.json' }
    $schema = ConvertFrom-Json -AsHashtable ([IO.File]::ReadAllText($schemaPath))
    # removeBy is optional since the owner decision of 2026-10-11. schema.json is a distributed bundle file with a pinned
    # hash, so it keeps removeBy in the required list. The checker takes it out of that list in memory, until a pin
    # synchronization changes the file.
    $findingDefinition = $schema.'$defs'.'baseline-finding'
    $findingDefinition.required = @($findingDefinition.required | Where-Object { $_ -cne 'removeBy' })
    $validation = @{ '$schema' = $schema.'$schema'; '$defs' = $schema.'$defs'; '$ref' = "#/`$defs/$Definition" }
    if (-not (Test-Json -Json $Text -Schema ($validation | ConvertTo-Json -Depth 100) -ErrorAction SilentlyContinue)) { throw "Invalid $Definition document (schema)" }
    return (ConvertFrom-Json -AsHashtable -Depth 100 $Text)
}
function Health-Path([string]$Path) {
    if ($Path.StartsWith('file:', [StringComparison]::OrdinalIgnoreCase)) { $Path = ([uri]$Path).LocalPath }
    else { $Path = [uri]::UnescapeDataString($Path) }
    $rootFull = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    $full = [IO.Path]::GetFullPath($(if ([IO.Path]::IsPathRooted($Path)) { $Path } else { [IO.Path]::Combine($rootFull, $Path) }))
    if (-not $full.StartsWith($rootFull + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw "Diagnostic path escapes repository: $Path" }
    return [IO.Path]::GetRelativePath($rootFull, $full).Replace('\', '/')
}
function Get-TrackedInventory([string]$GitRoot) {
    # Mode 160000 entries are submodule commits, not files.
    $all = @((Invoke-Checked 'git' @('-C', $GitRoot, 'ls-files', '-s', '-z', '--cached')) -split [char]0 | Where-Object { $_ -and $_ -notmatch '^160000 ' } | ForEach-Object { ($_ -split "`t", 2)[1] })
    return @($all | Where-Object { $_ } | ForEach-Object { [IO.Path]::GetFullPath((Join-Path $GitRoot $_)) } | Where-Object { $_.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) } | ForEach-Object { [IO.Path]::GetRelativePath($Root, $_).Replace('\', '/') })
}
function Get-HealthFiles {
    # Coverage includes untracked projects/configuration: adding a project must not evade the gate.
    $gitRoot = Get-Variable -Name GitRoot -Scope Script -ValueOnly -ErrorAction Ignore
    if ($gitRoot) {
        # Tracked plus untracked-not-ignored files. Ignored folders, such as a repository-local NuGet cache, are not scanned.
        foreach ($listed in ((Invoke-Checked 'git' @('-C', $gitRoot, 'ls-files', '-z', '--cached', '--others', '--exclude-standard')) -split [char]0)) {
            if (-not $listed) { continue }
            $full = [IO.Path]::GetFullPath((Join-Path $gitRoot $listed))
            if (-not $full.StartsWith($Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { continue }
            if (-not (Test-Path -LiteralPath $full -PathType Leaf)) { continue } # gitlinks and files deleted from the work tree
            $relative = [IO.Path]::GetRelativePath($Root, $full).Replace('\', '/')
            if (-not @($relative -split '/' | Where-Object { $_ -in @('.git', 'bin', 'obj', 'artifacts') }).Count) { $relative }
        }
        return
    }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($Root)
    while ($pending.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $pending.Pop() -Force) {
            if ($item.PSIsContainer) {
                if ($item.Name -notin @('.git', 'bin', 'obj', 'artifacts')) { $pending.Push($item.FullName) }
            }
            else { [IO.Path]::GetRelativePath($Root, $item.FullName).Replace('\', '/') }
        }
    }
}
function Read-EditorText([string]$Path) {
    return [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($Path))
}
function Test-ManagedBlock([string]$Common, [string]$Editor) {
    $begin = '# BEGIN CORE HEALTH MANAGED BLOCK' + "`n"
    $end = '# END CORE HEALTH MANAGED BLOCK'
    if ([regex]::Matches($Editor, [regex]::Escape($begin)).Count -ne 1 -or [regex]::Matches($Editor, [regex]::Escape($end)).Count -ne 1) { throw 'HC_DRIFT .editorconfig:1 missing/duplicate managed-block markers' }
    $start = $Editor.IndexOf($begin, [StringComparison]::Ordinal) + $begin.Length
    $stop = $Editor.IndexOf($end, [StringComparison]::Ordinal)
    if ($stop -lt $start -or $Editor.Substring($start, $stop - $start) -cne $Common) { throw 'HC_DRIFT .editorconfig:1 changed managed block (byte-for-byte UTF-8 LF required)' }
    Test-EditorOverrides $Common ($Editor.Remove($start - $begin.Length, $stop + $end.Length - $start + $begin.Length)) '.editorconfig'
}
function Test-EditorOverrides([string]$Common, [string]$Editor, [string]$Path) {
    # Preserve section-specific keys; conservatively protect common C# settings in every descendant section.
    $protected = @{}
    $section = ''
    foreach ($line in $Common -split "`n") {
        if ($line -match '^\[(.+)\]$') { $section = $Matches[1] }
        elseif ($line -match '^\s*([^#;=\s]+)\s*=\s*(.*?)\s*$' -and $section -eq '*.cs') { $protected[$Matches[1]] = $Matches[2] }
    }
    $ranks = @{ none = 0; silent = 1; suggestion = 2; warning = 3; error = 4 }
    # Roslyn drops a leading BOM when it reads the file, so the first line must be read the same way.
    $Editor = $Editor.TrimStart([char]0xFEFF)
    $lineNumber = 0
    $inPreamble = $true
    foreach ($line in $Editor -split '\r?\n') {
        $lineNumber++
        if ($line -match '^\s*\[.+\]\s*$') { $inPreamble = $false }
        # Roslyn accepts '=' or ':' as the separator and ignores a trailing '#' or ';' comment.
        if ($line -notmatch '^\s*([^#;=:\s]+)\s*[=:]\s*(.*?)\s*$') { continue }
        $key = $Matches[1]; $value = ($Matches[2] -replace '\s*[#;].*$', '').ToLowerInvariant()
        if ($Path -ne '.editorconfig' -and $inPreamble -and $key -eq 'root' -and $value -eq 'true') { throw "HC_DRIFT ${Path}:$lineNumber descendant root=true discards protected policy" }
        # Applies to every EditorConfig outside the managed block, including the root tail that follows it.
        if ($key -eq 'generated_code') { throw "HC_DRIFT ${Path}:$lineNumber generated_code is not allowed outside the managed block" }
        if ($key -like 'dotnet_analyzer_diagnostic.*') { throw "HC_DRIFT ${Path}:$lineNumber $key is not allowed outside the managed block" }
        if ($key -like 'dotnet_code_quality.*') { throw "HC_DRIFT ${Path}:$lineNumber $key narrows analysis and is not allowed outside the managed block" }
        if ($key -match '^dotnet_diagnostic\.[^.]+\.severity$' -and $value -in @('none', 'silent', 'suggestion')) { throw "HC_DRIFT ${Path}:$lineNumber weaker severity $key=$value" }
        if (-not $protected.ContainsKey($key)) { continue }
        $expected = $protected[$key]
        $stronger = $ranks.ContainsKey($value) -and $ranks.ContainsKey($expected) -and $ranks[$value] -ge $ranks[$expected]
        if ($value -cne $expected -and -not $stronger) { throw "HC_DRIFT ${Path}:$lineNumber weaker protected override $key=$value (required $expected)" }
    }
}
function Test-BundleDrift {
    $lockPath = Join-Path $Root 'eng/core-health.lock.json'
    $lock = Read-HealthDocument ([IO.File]::ReadAllText($lockPath)) 'core-health.lock'
    # A PR cannot replace the trusted pin. Pin updates require a separate synchronization change.
    if ($BaseRef) {
        $base = Invoke-Checked 'git' @('merge-base', 'HEAD', $BaseRef)
        $trusted = Read-HealthDocument (Invoke-Checked 'git' @('show', "${base}:eng/core-health.lock.json")) 'core-health.lock'
        if ($lock.coreCommit -cne $trusted.coreCommit) { throw 'HC_DRIFT eng/core-health.lock.json:1 pin differs from base; integrate the reviewed synchronization separately' }
        if (-not ($CoreRoot -or $Repo -eq 'core')) {
            # Lock-only mode has no canonical Core objects, so the trusted lock is the reference for the hashes.
            foreach ($name in $lock.files.Keys) { if ($lock.files[$name] -cne $trusted.files[$name]) { throw "HC_DRIFT eng/core-health.lock.json:1 hash of $name differs from base; integrate the reviewed synchronization separately" } }
        }
    }
    elseif ($Mode -ne 'Enroll') {
        $trusted = Read-HealthDocument (Invoke-Checked 'git' @('show', 'HEAD:eng/core-health.lock.json')) 'core-health.lock'
        if ($lock.coreCommit -cne $trusted.coreCommit) { throw 'HC_DRIFT eng/core-health.lock.json:1 pin differs from committed pin' }
    }
    $canonicalRoot = if ($CoreRoot) { [IO.Path]::GetFullPath($CoreRoot) } else { $Root }
    foreach ($name in $lock.files.Keys) {
        $path = Join-Path $Root "eng/core-health/$name"
        $bytes = [IO.File]::ReadAllBytes($path)
        if ($bytes -contains 13 -or ($bytes.Length -ge 3 -and $bytes[0] -eq 239 -and $bytes[1] -eq 187 -and $bytes[2] -eq 191)) { throw "HC_DRIFT $path`:1 LF/UTF-8 required" }
        $hash = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        if ($hash -cne $lock.files[$name]) { throw "HC_DRIFT $path`:1 SHA-256 differs from lock" }
        # Without -CoreRoot an application has no canonical Core objects. Only Core itself compares with its own tree.
        if (-not ($CoreRoot -or $Repo -eq 'core')) { continue }
        # Git blob identity is computed over raw bytes, without autocrlf filters.
        $canonical = (Invoke-Checked 'git' @('-C', $canonicalRoot, 'rev-parse', "$($lock.coreCommit):tools/repo-checks/csharp/$name")).Trim()
        $local = (Invoke-Checked 'git' @('hash-object', '--no-filters', $path)).Trim()
        if ($canonical -cne $local) { throw "HC_DRIFT $path`:1 differs from approved Core revision" }
        if ($Repo -eq 'core') {
            $working = (Invoke-Checked 'git' @('hash-object', '--no-filters', (Join-Path $Root "tools/repo-checks/csharp/$name"))).Trim()
            if ($working -cne $canonical) { throw "HC_DRIFT tools/repo-checks/csharp/$name`:1 canonical tree differs from pin" }
        }
    }
    $common = [IO.File]::ReadAllText((Join-Path $Root 'eng/core-health/.editorconfig'))
    Test-ManagedBlock $common (Read-EditorText (Join-Path $Root '.editorconfig'))
    # Only Core compares tools/repo-checks/csharp/.editorconfig with the pin, so only Core may skip it here.
    $exempt = @('eng/core-health/.editorconfig')
    if ($Repo -eq 'core') { $exempt += 'tools/repo-checks/csharp/.editorconfig' }
    foreach ($path in @(Get-HealthFiles | Where-Object { $_.EndsWith('/.editorconfig', [StringComparison]::OrdinalIgnoreCase) -and $_ -cnotin $exempt })) {
        Test-EditorOverrides $common (Read-EditorText (Join-Path $Root $path)) $path
    }
}
function Get-SdkDefaultIds {
    # Evaluated once: the NoWarn and WarningsNotAsErrors IDs that the SDK adds to every project.
    $cached = Get-Variable -Name SdkDefaultIds -Scope Script -ValueOnly -ErrorAction Ignore
    if ($cached) { return $cached }
    $pristineDirectory = Join-Path ([IO.Path]::GetTempPath()) ('repo-health-sdk-' + [guid]::NewGuid().ToString('N'))
    $sdkDefaults = @{ NoWarn = @(); WarningsNotAsErrors = @() }
    try {
        [void][IO.Directory]::CreateDirectory($pristineDirectory)
        $pristineProject = Join-Path $pristineDirectory 'Pristine.csproj'
        [IO.File]::WriteAllText($pristineProject, '<Project Sdk="Microsoft.NET.Sdk"><PropertyGroup><TargetFramework>net10.0</TargetFramework></PropertyGroup></Project>')
        # The pristine project must select the same SDK as the repository, so copy the nearest global.json.
        $sdkPinRoot = $Root
        while (-not (Test-Path -LiteralPath (Join-Path $sdkPinRoot 'global.json') -PathType Leaf)) {
            $sdkPinParent = Split-Path $sdkPinRoot -Parent
            if (-not $sdkPinParent -or $sdkPinParent -eq $sdkPinRoot) { throw 'global.json is required to evaluate the SDK defaults' }
            $sdkPinRoot = $sdkPinParent
        }
        Copy-Item -LiteralPath (Join-Path $sdkPinRoot 'global.json') -Destination (Join-Path $pristineDirectory 'global.json')
        $pristine = ConvertFrom-Json -AsHashtable -InputObject (Invoke-Checked 'dotnet' @('msbuild', $pristineProject, '-nologo', '-p:NuGetAudit=false', '-getProperty:NoWarn,WarningsNotAsErrors'))
        foreach ($property in $sdkDefaults.Keys.Clone()) { $sdkDefaults[$property] = @($pristine.Properties[$property] -split '[;,\s]+' | Where-Object { $_ }) }
    }
    finally {
        if ([IO.Path]::GetFullPath((Split-Path $pristineDirectory -Parent)).TrimEnd([IO.Path]::DirectorySeparatorChar) -eq [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd([IO.Path]::DirectorySeparatorChar) -and (Test-Path -LiteralPath $pristineDirectory)) { [IO.Directory]::Delete($pristineDirectory, $true) }
    }
    $script:SdkDefaultIds = $sdkDefaults
    return $sdkDefaults
}
function Health-Ids([string]$Value) { return (@($Value -split '[;,\s]+' | Where-Object { $_ } | Sort-Object -Unique) -join ';') }
function Get-LedgerWarningIds($Baseline, [string]$Project) {
    if ($null -eq $Baseline) { return '' }
    return (@($Baseline.findings | Where-Object { $_.project -ceq $Project -and $_.rule -notin $script:SyntaxRules -and -not $_.rule.StartsWith('FORMAT:') } | ForEach-Object { $_.rule } | Sort-Object -Unique) -join ';')
}
function Test-ProjectProperties($Evaluation, [string]$Path, $Baseline) {
    $p = $Evaluation.Properties
    if ($p.HealthLayer -notin @('domain', 'application', 'presentation', 'infrastructure', 'composition', 'tests', 'tooling')) { throw "HC_COVERAGE ${Path}:1 missing/invalid HealthLayer '$($p.HealthLayer)'" }
    foreach ($pair in @{ TreatWarningsAsErrors = 'true'; Nullable = 'enable'; AnalysisLevel = 'latest-recommended'; LangVersion = '14.0'; EnableNETAnalyzers = 'true'; EnforceCodeStyleInBuild = 'true'; Deterministic = 'true'; RestorePackagesWithLockFile = 'true'; ImplicitUsings = 'enable' }.GetEnumerator()) {
        if ($p[$pair.Key] -cne $pair.Value) { throw "HC_CONFIGURATION ${Path}:1 evaluated $($pair.Key)=$($p[$pair.Key]); required $($pair.Value)" }
    }
    foreach ($key in @('RunAnalyzers', 'RunAnalyzersDuringBuild')) { if ($p[$key] -notin @('', 'true')) { throw "HC_CONFIGURATION ${Path}:1 evaluated $key disables analyzers" } }
    # SDK-added mandatory error IDs; any replacement/removal is detected.
    if ((Health-Ids $p.WarningsAsErrors) -cne 'NU1605;SYSLIB0011') { throw "HC_CONFIGURATION ${Path}:1 evaluated WarningsAsErrors override" }
    $ids = Health-Ids $p.HealthBaselineWarningIds
    if ($ids -match '(^|;)(VSTHRD100|CS4014|RS0016|RS0017|RS0024|RS0025|RS0036|RS0037)(;|$)') { throw "HC_CONFIGURATION ${Path}:1 error rule cannot use a warning debt exemption" }
    if ((Health-Ids $p.WarningsNotAsErrors) -cne $ids) { throw "HC_CONFIGURATION ${Path}:1 WarningsNotAsErrors exceeds ledger warning IDs" }
    if ($Baseline -and $ids -cne (Get-LedgerWarningIds $Baseline $p.MSBuildProjectName)) { throw "HC_CONFIGURATION ${Path}:1 HealthBaselineWarningIds differs from ledger" }
    if ($Baseline) {
        $allowedNoWarn = @($Baseline.findings | Where-Object { $_.project -ceq $p.MSBuildProjectName -and $_.rule -eq 'suppressions' -and $_.symbol.StartsWith('NoWarn:') } | ForEach-Object { $_.symbol.Substring(7) }) -join ';'
        $sdkNoWarn = (Get-SdkDefaultIds).NoWarn
        $projectNoWarn = (@($p.NoWarn -split '[;,\s]+' | Where-Object { $_ -and $_ -notin $sdkNoWarn }) -join ';')
        if ((Health-Ids $projectNoWarn) -cne (Health-Ids $allowedNoWarn)) { throw "HC_CONFIGURATION ${Path}:1 evaluated NoWarn differs from suppression ledger" }
    }
    $packages = @{ 'Microsoft.CodeAnalysis.BannedApiAnalyzers' = '3.3.4'; 'Microsoft.VisualStudio.Threading.Analyzers' = '17.14.15' }
    if ($p.HealthPublicApi -eq 'true') { $packages['Microsoft.CodeAnalysis.PublicApiAnalyzers'] = '3.3.4' }
    foreach ($name in $packages.Keys) {
        $refs = @($Evaluation.Items.PackageReference | Where-Object { $_.Identity -ceq $name })
        $versions = @($Evaluation.Items.PackageVersion | Where-Object { $_.Identity -ceq $name })
        if ($refs.Count -ne 1 -or $versions.Count -ne 1 -or $versions[0].Version -cne $packages[$name]) { throw "HC_CONFIGURATION ${Path}:1 analyzer package missing/changed: $name" }
        $r = $refs[0]
        if ($r.PrivateAssets -cne 'all' -or $r.IncludeAssets -cne 'runtime;build;native;contentfiles;analyzers;buildtransitive' -or ($r.Contains('ExcludeAssets') -and $r.ExcludeAssets) -or ($r.Contains('VersionOverride') -and $r.VersionOverride) -or ($r.Contains('Version') -and $r.Version -cne $packages[$name])) { throw "HC_CONFIGURATION ${Path}:1 analyzer package override: $name" }
    }
    foreach ($name in @('BannedSymbols.Common.txt', 'BannedSymbols.Files.txt') + $(if ($p.HealthLayer -eq 'tests') { @('BannedSymbols.Tests.txt') } else { @() })) {
        $required = [IO.Path]::GetFullPath((Join-Path $Root "eng/core-health/$name"))
        if (-not @($Evaluation.Items.AdditionalFiles | Where-Object { [IO.Path]::GetFullPath($_.FullPath) -eq $required }).Count) { throw "HC_CONFIGURATION ${Path}:1 missing protected AdditionalFiles $name" }
    }
    if ($p.HealthLayer -in @('domain', 'application')) {
        $pure = Join-Path $Root "eng/code-health/$($p.HealthLayer)/BannedSymbols.txt"
        if (-not (Test-Path -LiteralPath $pure) -or 'T:System.IO.File' -cnotin [IO.File]::ReadAllLines($pure)) { throw "HC_CONFIGURATION ${Path}:1 pure layer must ban System.IO.File" }
    }
    $expected = [IO.Path]::GetFullPath((Join-Path $Root "artifacts/code-health/$($p.MSBuildProjectName).sarif"))
    if ($p.ErrorLog -notmatch '^(.+),version=2\.1$' -or [IO.Path]::GetFullPath($Matches[1]) -cne $expected) { throw "HC_CONFIGURATION ${Path}:1 ErrorLog override/collision" }
}
function Get-ProjectCoverage($Baseline) {
    $properties = 'MSBuildProjectName,HealthLayer,HealthPublicApi,HealthBaselineWarningIds,TreatWarningsAsErrors,Nullable,AnalysisLevel,LangVersion,EnableNETAnalyzers,EnforceCodeStyleInBuild,RunAnalyzers,RunAnalyzersDuringBuild,Deterministic,RestorePackagesWithLockFile,ImplicitUsings,WarningsAsErrors,WarningsNotAsErrors,NoWarn,ErrorLog,Configurations,TargetFramework,TargetFrameworks,DefineConstants,TargetPath,ProjectAssetsFile'
    $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $result = @()
    foreach ($path in @(Get-HealthFiles | Where-Object { $_.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase) } | Sort-Object)) {
        $first = ConvertFrom-Json -AsHashtable (Invoke-Checked 'dotnet' @('msbuild', $path, '-nologo', '-p:NuGetAudit=false', '-p:Configuration=Release', '-p:HealthCollectDiagnostics=true', "-getProperty:$properties", '-getItem:PackageReference,PackageVersion,Compile,ProjectReference,AdditionalFiles'))
        $p = $first.Properties
        if (-not $names.Add($p.MSBuildProjectName)) { throw "HC_COVERAGE ${path}:1 duplicate project name/SARIF filename" }
        $frameworks = @($(if ($p.TargetFrameworks) { $p.TargetFrameworks } else { $p.TargetFramework }) -split ';' | Where-Object { $_ })
        if ($frameworks.Count -ne 1) { throw "HC_COVERAGE ${path}:1 multi-target projects require separate per-TFM SARIF paths before adoption" }
        $configs = @(@('Debug', 'Release') + @($p.Configurations -split ';') | Where-Object { $_ } | Sort-Object -Unique)
        foreach ($config in $configs) {
            $e = if ($config -eq 'Release') { $first } else { ConvertFrom-Json -AsHashtable (Invoke-Checked 'dotnet' @('msbuild', $path, '-nologo', '-p:NuGetAudit=false', "-p:Configuration=$config", '-p:HealthCollectDiagnostics=true', "-getProperty:$properties", '-getItem:PackageReference,PackageVersion,Compile,ProjectReference,AdditionalFiles')) }
            Test-ProjectProperties $e $path $Baseline
        }
        $result += @{ path = $path; name = $p.MSBuildProjectName; layer = $p.HealthLayer; evaluation = $first }
    }
    if (-not $result.Count) { throw 'HC_COVERAGE repository:1 no C# projects' }
    return ,$result
}
function Add-DiagnosticRequest([string]$Rule, [string]$Project, [string]$Path, [int]$Line, [int]$Column, [string]$Symbol, [string]$Stage) {
    $script:DiagnosticRequests.Add(@{ rule = $Rule; project = $Project; path = $Path; line = $Line; column = $Column; symbol = $Symbol; stage = $Stage })
}
function Read-ProjectSarif([string]$Path, [string]$Project, [string]$ProjectPath, [switch]$RequireAnalyzerMetadata) {
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "HC_SARIF ${ProjectPath}:1 missing SARIF $Path" }
    try { $s = ConvertFrom-Json -AsHashtable -Depth 100 ([IO.File]::ReadAllText($Path)) } catch { throw "HC_SARIF ${ProjectPath}:1 unreadable SARIF: $_" }
    if ($s.version -cne '2.1.0' -or $s.runs -isnot [array] -or $s.runs.Count -ne 1 -or $s.runs[0].results -isnot [array]) { throw "HC_SARIF ${ProjectPath}:1 invalid/failed project output" }
    if ($RequireAnalyzerMetadata) {
        $ids = @($s.runs[0].tool.driver.rules | ForEach-Object { $_.id })
        foreach ($id in @('RS0030', 'VSTHRD100')) { if ($id -cnotin $ids) { throw "HC_SARIF ${ProjectPath}:1 protected analyzer did not load ($id)" } }
    }
    foreach ($run in $s.runs) {
        if ($run.Contains('invocations')) {
            foreach ($inv in $run.invocations) {
                if (($inv.Contains('executionSuccessful') -and $inv.executionSuccessful -ne $true) -or ($inv.Contains('toolExecutionNotifications') -and @($inv.toolExecutionNotifications | Where-Object { $_.level -eq 'error' }).Count)) { throw "HC_SARIF ${ProjectPath}:1 failed project load/tool invocation" }
            }
        }
        foreach ($r in $run.results) {
            if ($r.ruleId -in @('AD0001', 'AD0002', 'VSTHRD100', 'CS4014', 'RS0016', 'RS0017', 'RS0024', 'RS0025', 'RS0036', 'RS0037')) { throw "HC_SARIF ${ProjectPath}:1 analyzer/protected error failure $($r.ruleId)" }
            # Suppressed warnings may be encoded as error after warnings-as-errors.
            # The native build must still succeed. Tracked suppression scopes are
            # independently ratcheted; generated obj source remains out of scope.
            if ($r.Contains('suppressions') -and $r.suppressions.Count) { continue }
            if ($r.level -eq 'error') { throw "HC_SARIF ${ProjectPath}:1 analyzer/compiler failure $($r.ruleId)" }
            if ($r.level -notin @('warning', 'note', 'none')) { throw "HC_SARIF ${ProjectPath}:1 unknown diagnostic level" }
            if ($r.level -ne 'warning') { continue }
            $symbol = $r.ruleId
            if ($r.ruleId -eq 'RS0030') {
                $quoted = [regex]::Matches($r.message.text, "'([^']+)'")
                if ($quoted.Count -ne 1) { throw "HC_SARIF ${ProjectPath}:1 cannot parse RS0030 banned symbol" }
                $symbol = $quoted[0].Groups[1].Value
            }
            if (-not $r.Contains('locations') -or -not $r.locations.Count) { Add-DiagnosticRequest $r.ruleId $Project $ProjectPath 1 1 $r.message.text 'build'; continue }
            $location = $r.locations[0].physicalLocation
            if ($location.artifactLocation.Contains('uriBaseId')) { throw 'HC_SARIF unresolved URI base' }
            $source = Health-Path $location.artifactLocation.uri
            Add-DiagnosticRequest $r.ruleId $Project $source $location.region.startLine $location.region.startColumn $symbol 'build'
        }
    }
}
function Read-FormatReport([string]$Path, [int]$ExitCode, [string]$Stage, [string]$Project = '') {
    if ($ExitCode -notin @(0, 2)) { throw "HC_FORMAT $Path`:1 tool crashed/failed with exit $ExitCode" }
    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) { throw "HC_FORMAT $Path`:1 missing format report" }
    try { $report = ConvertFrom-Json -NoEnumerate -AsHashtable -Depth 100 ([IO.File]::ReadAllText($Path)) } catch { throw "HC_FORMAT $Path`:1 unreadable format report" }
    if ($report -isnot [array]) { throw "HC_FORMAT $Path`:1 expected structured array" }
    $before = $script:DiagnosticRequests.Count
    foreach ($file in $report) {
        if (-not $file.Contains('FilePath') -or $file.FileChanges -isnot [array]) { throw "HC_FORMAT $Path`:1 invalid file report" }
        $source = Health-Path $file.FilePath
        $owners = @($script:HealthProjects | Where-Object { @($_.evaluation.Items.Compile | Where-Object { [IO.Path]::GetFullPath($_.FullPath) -eq [IO.Path]::GetFullPath((Join-Path $Root $source)) }).Count })
        if ($Project) { $owners = @($owners | Where-Object { $_.name -ceq $Project }) }
        if (-not $owners.Count) { throw "HC_FORMAT ${source}:1 no evaluated project owns this report" }
        $used = [Collections.Generic.HashSet[string]]::new()
        foreach ($change in $file.FileChanges) {
            if (-not $change.DiagnosticId -or $change.LineNumber -lt 1 -or $change.CharNumber -lt 1 -or -not $change.FormatDescription) { throw "HC_FORMAT ${source}:1 malformed change" }
            foreach ($owner in $owners) {
                # dotnet format reports unfixable analyzer warnings after its prospective
                # edits, so these coordinates can have moved. Reuse fresh compiler
                # occurrences in source order; build debt still independently gates them.
                $candidates = @($script:DiagnosticRequests | Where-Object { $_.stage -ceq 'build' -and $_.project -ceq $owner.name -and $_.path -ceq $source -and $_.rule -ceq $change.DiagnosticId -and -not $used.Contains("$($_.project)|$($_.rule)|$($_.line)|$($_.column)") } | Sort-Object { [Math]::Abs($_.line - $change.LineNumber) }, line, column)
                if ($change.FormatDescription -match '^(warning|error) [A-Za-z0-9]+:' -and $candidates.Count) {
                    $f = $candidates[0].Clone()
                    [void]$used.Add("$($f.project)|$($f.rule)|$($f.line)|$($f.column)")
                    $f.stage = $Stage; $f['reuse'] = $true
                    $script:DiagnosticRequests.Add($f)
                }
                else { Add-DiagnosticRequest ("FORMAT:" + $change.DiagnosticId) $owner.name $source $change.LineNumber $change.CharNumber $change.DiagnosticId $Stage }
            }
        }
    }
    if ($ExitCode -ne 0 -and $script:DiagnosticRequests.Count -eq $before) { throw "HC_FORMAT $Path`:1 nonzero exit without findings (project-load/tool failure)" }
}
function Test-SeamOwner($Finding, $Entries) {
    return @($Entries | Where-Object { $_.path -ceq $Finding.path -and ($_.member -ceq '*' -or $_.member -ceq $Finding.member) -and $_.symbol -ceq $Finding.symbol }).Count -gt 0
}
function Test-FormatExit([int]$ExitCode, $Findings, $Baseline) {
    if ($ExitCode -eq 0) { return }
    if ($ExitCode -ne 2 -or -not $Findings.Count) { throw "HC_FORMAT tool failure exit=$ExitCode" }
    if ($Mode -eq 'Enroll') { return } # Explicit, one-time owner enrollment after every tool succeeded.
    $allowed = @{}
    foreach ($f in $Baseline.findings) { $allowed[(Finding-Key $f)] = $f.count }
    foreach ($f in $Findings) {
        if ($f.rule -eq 'RS0030' -and (Test-SeamOwner $f $script:Seams.entries)) { continue }
        $key = Finding-Key $f
        if (-not $allowed.ContainsKey($key) -or $f.count -gt $allowed[$key]) { Write-Failure 'HC_DIAGNOSTIC' $f $f.rule $(if ($allowed.ContainsKey($key)) { $allowed[$key] } else { 0 }) $f.count 'New format fingerprint from nonzero format exit.' | ForEach-Object { Write-Host $_ } }
    }
}
function Initialize-Enforcement($Baseline) {
    if (-not $Solution -or -not (Test-Path -LiteralPath (Join-Path $Root $Solution) -PathType Leaf)) { throw 'Solution is required for Enroll/Verify/LowerBaseline' }
    Test-BundleDrift
    $script:Seams = Read-HealthDocument ([IO.File]::ReadAllText((Join-Path $Root 'eng/code-health/seam-owners.json'))) 'seam-owners'
    $script:HealthProjects = Get-ProjectCoverage $Baseline
    # Fresh output only: delete named outputs, never recursively remove computed paths.
    [void][IO.Directory]::CreateDirectory((Join-Path $Root 'artifacts/code-health'))
    foreach ($project in $script:HealthProjects) {
        $sarif = Join-Path $Root "artifacts/code-health/$($project.name).sarif"
        if (Test-Path -LiteralPath $sarif) { Remove-Item -LiteralPath $sarif }
    }
    [void](Invoke-Checked 'dotnet' @('build', $Solution, '-c', 'Release', '--no-restore', '--no-incremental', '-p:HealthCollectDiagnostics=true'))
    foreach ($project in $script:HealthProjects) { Read-ProjectSarif (Join-Path $Root "artifacts/code-health/$($project.name).sarif") $project.name $project.path -RequireAnalyzerMetadata }
    $script:FormatRuns = @()
    $runs = @(@{ target = $Solution; directory = 'format'; project = ''; style = $false })
    foreach ($project in @($script:HealthProjects | Where-Object { $_.layer -eq 'tests' })) { $runs += @{ target = $project.path; directory = "format-style-$($project.name)"; project = $project.name; style = $true } }
    foreach ($run in $runs) {
        $dir = "artifacts/code-health/$($run.directory)"
        $report = Join-Path $Root "$dir/format-report.json"
        if (Test-Path -LiteralPath $report) { Remove-Item -LiteralPath $report }
        $args = @('format')
        if ($run.style) { $args += 'style' }
        $args += @($run.target, '--verify-no-changes', '--no-restore', '--severity', 'warn', '--report', $dir)
        if ($run.style) { $args += @('--diagnostics', 'IDE0005') }
        $output = @(& dotnet @args 2>&1)
        $code = $LASTEXITCODE
        [IO.File]::WriteAllLines((Join-Path $Root "artifacts/code-health/$($run.directory).log"), [string[]]$output)
        if (($output -join "`n") -match '(?i)(failed to load|error loading|unhandled exception|exception was thrown|could not load|warnings were encountered while loading the workspace|workspace warnings|unable to load)') { throw "HC_FORMAT $dir`:1 failed project load/tool execution; see log" }
        Read-FormatReport $report $code $run.directory $run.project
        $script:FormatRuns += @{ stage = $run.directory; exitCode = $code }
    }
}
function Measure-Health {
    $m = Measure-Syntax
    $diagnostics = @($script:ResolvedDiagnostics)
    $allowedSeams = @($diagnostics | Where-Object { $_.rule -eq 'RS0030' -and (Test-SeamOwner $_ $script:Seams.entries) })
    $diagnostics = @($diagnostics | Where-Object { $_ -notin $allowedSeams })
    $m.findings += @($diagnostics | Where-Object { -not $_.reuse } | Group-Object -CaseSensitive { Finding-Key $_ } | ForEach-Object { $f = $_.Group[0]; $f.count = [int]($_.Group | Measure-Object count -Sum).Sum; $f })
    foreach ($run in $script:FormatRuns) { Test-FormatExit $run.exitCode @($script:ResolvedDiagnostics | Where-Object { $_.stage -ceq $run.stage } | Group-Object -CaseSensitive { Finding-Key $_ } | ForEach-Object { $f = $_.Group[0].Clone(); $f.count = if ($f.reuse) { @($_.Group | Group-Object -CaseSensitive { "$($_.path)|$($_.line)|$($_.column)" }).Count } else { $_.Count }; $f }) $baseline }
    $m['authorizedSeams'] = $allowedSeams
    if ($allowedSeams.Count) { Write-Host "HC_DIAGNOSTIC seam-owners:1 authorized RS0030 occurrences=$($allowedSeams.Count); separate from debt" }
    $m.extensions = @{ build = 'Release; no restore; no incremental'; sarif = 'per project; fresh output'; format = 'structured fingerprints; IDE0005 test style pass'; projectCoverage = @($script:HealthProjects | ForEach-Object { @{ project = $_.name; path = $_.path; layer = $_.layer } }) }
    return $m
}
function Write-LedgerWarningIds($Baseline) {
    $path = Join-Path $Root 'eng/code-health/projects.props'
    $text = [IO.File]::ReadAllText($path)
    $marker = '  <!-- BEGIN GENERATED HEALTH WARNING IDS -->'
    $end = '  <!-- END GENERATED HEALTH WARNING IDS -->'
    $lines = @($marker)
    foreach ($project in @($script:HealthProjects | Sort-Object name)) {
        $ids = Get-LedgerWarningIds $Baseline $project.name
        if ($ids) { $lines += "  <PropertyGroup Condition=`"'`$(MSBuildProjectName)' == '$($project.name)'`">", "    <HealthBaselineWarningIds>$ids</HealthBaselineWarningIds>", '  </PropertyGroup>' }
    }
    $lines += $end
    if (-not $text.Contains($marker) -or -not $text.Contains($end)) { throw 'Missing generated warning-ID block in projects.props' }
    $pattern = '(?s)' + [regex]::Escape($marker) + '.*?' + [regex]::Escape($end)
    [IO.File]::WriteAllText($path, [regex]::Replace($text, $pattern, ($lines -join "`n")))
}
function Get-EnrollOwner {
    # Core enrolls as NVT CORE. Other repositories default to their -Repo name in upper case; -Owner overrides both.
    if ($Owner.Trim()) { return $Owner.Trim() }
    if ($Repo -ceq 'core') { return 'NVT CORE' }
    return $Repo.ToUpperInvariant()
}
function Enroll-Baseline($Measurement, [string]$Path) {
    $enrollOwner = Get-EnrollOwner
    if (Test-Path -LiteralPath $Path) { throw 'Enroll refuses: baseline already exists' }
    $b = [ordered]@{ schemaVersion = 1; measurementVersion = $script:Version; snapshotCommit = $script:Snapshot; limits = $script:Limits.Clone(); entities = @(); findings = @() }
    foreach ($e in $Measurement.entities) {
        $ceilings = @{}
        foreach ($metric in $e.values.Keys) { if ($e.values[$metric] -gt $script:Limits[$metric]) { $ceilings[$metric] = $e.values[$metric] } }
        if ($ceilings.Count) { $b.entities += @{ project = $e.project; kind = $e.kind; symbol = $e.symbol; locations = $e.locations; contentHash = $e.contentHash; ceilings = $ceilings; owner = $enrollOwner; issue = 'health-refactor/2026-10-31' } }
    }
    foreach ($f in $Measurement.findings) { $entry = @{ owner = $enrollOwner }; foreach ($key in @('rule', 'project', 'path', 'member', 'symbol', 'syntaxHash', 'count')) { $entry[$key] = $f[$key] }; $b.findings += $entry }
    $json = ConvertTo-Json -Depth 100 $b
    [void](Read-HealthDocument $json 'baseline')
    [void][IO.Directory]::CreateDirectory((Split-Path $Path -Parent))
    # CreateNew prevents a concurrent enrollment from overwriting an existing ledger.
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes($json + "`n"); $stream.Write($bytes) } finally { $stream.Dispose() }
    Write-LedgerWarningIds $b
    Write-Output "HC_STATE $BaselinePath`:1 Enroll passed; $($b.entities.Count) structural entities, $($b.findings.Count) finding fingerprints."
}

function Split-TestFindings($Measurement) {
    # Test-duplication findings have their own ledger, so the main baseline never sees them.
    $Measurement['testFindings'] = @($Measurement.findings | Where-Object { $_.rule -cin $script:TestRules })
    $Measurement.findings = @($Measurement.findings | Where-Object { $_.rule -cnotin $script:TestRules })
}
function Read-TestDebt([string]$Text, [string]$Label) {
    try { $d = ConvertFrom-Json -InputObject $Text -AsHashtable -Depth 100 }
    catch { throw "Bad test-debt $Label`: invalid JSON: $($_.Exception.Message)" }
    if ($d -isnot [System.Collections.IDictionary]) { throw "Bad test-debt $Label`: expected object" }
    $allowedKeys = @('schemaVersion', 'measurementVersion', 'snapshotCommit', 'findings')
    foreach ($key in @($d.Keys)) { if ($key -cnotin $allowedKeys) { throw "Bad test-debt $Label`: unknown $key" } }
    foreach ($key in $allowedKeys) { if (-not $d.Contains($key)) { throw "Bad test-debt $Label`: missing $key" } }
    if (-not (Test-Count $d.schemaVersion) -or $d.schemaVersion -ne 1 -or $d.measurementVersion -cne $script:Version) { throw "Bad test-debt $Label`: schema/measurement migration required" }
    if ($d.snapshotCommit -isnot [string] -or $d.snapshotCommit -notmatch '^[0-9a-fA-F]{7,40}$') { throw "Bad test-debt $Label`: invalid snapshotCommit" }
    if ($d.findings -isnot [array]) { throw "Bad test-debt $Label`: findings must be an array" }
    $seen = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($f in $d.findings) {
        foreach ($key in @('rule', 'project', 'path', 'member', 'symbol', 'syntaxHash', 'count', 'owner')) {
            if ($f -isnot [System.Collections.IDictionary] -or -not $f.Contains($key)) { throw "Bad test-debt $Label`: finding missing $key" }
            if ($key -ne 'count' -and ($f[$key] -isnot [string] -or -not $f[$key].Trim())) { throw "Bad test-debt $Label`: invalid finding $key" }
        }
        if ($f.rule -cnotin $script:TestRules) { throw "Bad test-debt $Label`: $($f.rule) is not a test-duplication rule" }
        if (-not (Test-Count $f.count) -or $f.count -eq 0 -or $f.syntaxHash -notmatch '^[0-9a-f]{64}$') { throw "Bad test-debt $Label`: invalid finding count/hash/date" }
        if ($f.Contains('removeBy') -and -not (Test-IsoDate $f.removeBy)) { throw "Bad test-debt $Label`: invalid finding count/hash/date" }
        if (-not $seen.Add((Finding-Key $f))) { throw "Bad test-debt $Label`: duplicate finding fingerprint" }
    }
    return $d
}
function Compare-TestDebtLedger($Allowed, $Current) {
    # The ledger of the base ref is the ceiling. Without one, this change is the first enrollment.
    if ($null -eq $Allowed) { return }
    if ($null -eq $Current) {
        Write-Failure 'HC_STATE' @{ locations = @($TestDebtPath); project = $Repo; symbol = 'test-debt' } 'test-debt' 'present' 'deleted' 'The test-debt ledger cannot be deleted.'
        return
    }
    $was = @{}
    foreach ($f in $Allowed.findings) { $was[(Finding-Key $f)] = $f.count }
    foreach ($f in $Current.findings) {
        $old = if ($was.ContainsKey((Finding-Key $f))) { $was[(Finding-Key $f)] } else { 0 }
        if ($f.count -gt $old) { Write-Failure 'HC_STATE' $f $f.rule $old $f.count 'The test-debt ledger may only shrink after its first enrollment.' }
    }
}
function Compare-TestDebtMeasurement($Ledger, $Measurement, [bool]$RequireLower) {
    if ($null -eq $Ledger) {
        if (@($Measurement.testFindings).Count) {
            Write-Failure 'HC_STATE' @{ locations = @($TestDebtPath); project = $Repo; symbol = 'test-debt' } 'test-debt' 'missing' "$(@($Measurement.testFindings).Count) fingerprints" 'The test-debt ledger is missing. Run -Mode EnrollTestDebt once to seed it.'
        }
        return
    }
    $recorded = @{}
    foreach ($f in $Ledger.findings) { $recorded[(Finding-Key $f)] = $f.count }
    $measured = @{}
    foreach ($f in $Measurement.testFindings) {
        $key = Finding-Key $f
        $measured[$key] = $f.count
        $limit = if ($recorded.ContainsKey($key)) { $recorded[$key] } else { 0 }
        if ($f.count -gt $limit) { Write-Failure 'HC_DIAGNOSTIC' $f $f.rule $limit $f.count 'New test duplication. Use the Nvt.Core.TestSupport helper (SignalWait, ManualTimeProvider, TestWorkspace, ChildProcessFixture) or one shared fake. The ledger cannot be raised.' }
    }
    if ($RequireLower) {
        foreach ($f in $Ledger.findings) {
            $now = if ($measured.ContainsKey((Finding-Key $f))) { $measured[(Finding-Key $f)] } else { 0 }
            if ($now -lt $f.count) { Write-Failure 'HC_DIAGNOSTIC' $f $f.rule $f.count $now 'Fixes must lower/delete test-debt in this change; run LowerBaseline.' }
        }
    }
}
function Enroll-TestDebt($Measurement, [string]$Path) {
    if (Test-Path -LiteralPath $Path) { throw 'EnrollTestDebt refuses: the ledger already exists' }
    $enrollOwner = Get-EnrollOwner
    $entries = @()
    foreach ($f in @($Measurement.testFindings | Sort-Object { Finding-Key $_ })) {
        $entry = @{ owner = $enrollOwner }
        foreach ($key in @('rule', 'project', 'path', 'member', 'symbol', 'syntaxHash', 'count')) { $entry[$key] = $f[$key] }
        $entries += $entry
    }
    $ledger = [ordered]@{ schemaVersion = 1; measurementVersion = $script:Version; snapshotCommit = $script:Snapshot; findings = $entries }
    $json = ConvertTo-Json -Depth 100 $ledger
    [void](Read-TestDebt $json 'test-debt')
    [void][IO.Directory]::CreateDirectory((Split-Path $Path -Parent))
    $stream = [IO.File]::Open($Path, [IO.FileMode]::CreateNew)
    try { $bytes = [Text.Encoding]::UTF8.GetBytes($json + "`n"); $stream.Write($bytes) } finally { $stream.Dispose() }
    Write-Output "HC_STATE $TestDebtPath`:1 EnrollTestDebt passed; $($entries.Count) test-duplication fingerprints."
}
function Lower-TestDebt($Ledger, $Measurement, [string]$Path) {
    if ($null -eq $Ledger) { return }
    $now = @{}
    foreach ($f in $Measurement.testFindings) { $now[(Finding-Key $f)] = $f.count }
    $Ledger.findings = @($Ledger.findings | Where-Object { $now.ContainsKey((Finding-Key $_)) } | ForEach-Object { $_.count = $now[(Finding-Key $_)]; $_ })
    [IO.File]::WriteAllText($Path, (ConvertTo-Json -InputObject $Ledger -Depth 100) + "`n")
}

$hostSource = @'
using System;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Security.Cryptography;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

public static class HealthSyntaxHost
{
    static string Hash(string s) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(s.Replace("\r\n", "\n").Replace('\r', '\n')))).ToLowerInvariant();
    static string Normal(SyntaxNode n) => string.Join(" ", n.DescendantTokens().Select(t => t.Text));
    static string Name(SimpleNameSyntax n) => n.Identifier.ValueText;
    static string Call(InvocationExpressionSyntax n) => n.Expression is MemberAccessExpressionSyntax m ? Name(m.Name) : n.Expression is SimpleNameSyntax s ? Name(s) : n.Expression is MemberBindingExpressionSyntax b ? Name(b.Name) : "";
    static bool Modifier(SyntaxTokenList ts, SyntaxKind k) => ts.Any(t => t.IsKind(k));
    // Convention T2: the shared test-support project is the only owner of the fake clock, the workspace and the blocking wait.
    static bool OwnsSharedHelpers(string path, string project) =>
        project.Equals("TestSupport", StringComparison.OrdinalIgnoreCase) || project.EndsWith(".TestSupport", StringComparison.OrdinalIgnoreCase)
        || path.Replace('\\', '/').Split('/').SkipLast(1).Any(p => p.Equals("TestSupport", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".TestSupport", StringComparison.OrdinalIgnoreCase));
    static bool IsTestPath(string path, string project)
    {
        string[] parts = path.Replace('\\', '/').Split('/');
        // The shared helper project is the one sanctioned place for these patterns.
        bool support = project.Equals("TestSupport", StringComparison.OrdinalIgnoreCase) || project.EndsWith(".TestSupport", StringComparison.OrdinalIgnoreCase)
            || parts.Take(parts.Length - 1).Any(p => p.Equals("TestSupport", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".TestSupport", StringComparison.OrdinalIgnoreCase));
        if (support) return false;
        return project.EndsWith("Tests", StringComparison.OrdinalIgnoreCase)
            || parts.Take(parts.Length - 1).Any(p => p.Equals("tests", StringComparison.OrdinalIgnoreCase) || p.Equals("test", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".Tests", StringComparison.OrdinalIgnoreCase) || p.EndsWith(".Test", StringComparison.OrdinalIgnoreCase));
    }
    static readonly string[] FakePrefixes = { "Fake", "Stub", "Spy", "Mock", "Recording", "Dummy" };
    static bool IsFakeName(string name) => FakePrefixes.Any(p => name.Length > p.Length && name.StartsWith(p, StringComparison.Ordinal) && char.IsUpper(name[p.Length]));
    static string Qualified(SyntaxNode n)
    {
        var parts = new List<string>();
        foreach (var a in n.AncestorsAndSelf().Reverse())
        {
            if (a is BaseNamespaceDeclarationSyntax ns) parts.Add(ns.Name.ToString().Replace("global::", ""));
            if (a is BaseTypeDeclarationSyntax t)
            {
                int arity = t is TypeDeclarationSyntax td ? td.TypeParameterList?.Parameters.Count ?? 0 : 0;
                parts.Add(t.Identifier.ValueText + (arity == 0 ? "" : "`" + arity));
            }
        }
        return string.Join(".", parts);
    }
    static bool Callable(SyntaxNode n) => n is BaseMethodDeclarationSyntax || n is AccessorDeclarationSyntax || n is LocalFunctionStatementSyntax;
    // A code-only view derives from an Avalonia view base type, directly or through a base type of the same project.
    // It counts for viewTypeLines only above the budget, so a small view adds no entity and no app needs a one-time registration.
    // The checker has no Avalonia assemblies, so an Avalonia base type is an error type and matches by simple name.
    // The names are the Avalonia.Controls types that app views derive from. An unresolved name counts only in a project
    // whose source mentions Avalonia. A resolved base type must be in the Avalonia namespace.
    static readonly HashSet<string> ViewBases = new(StringComparer.Ordinal) { "Control", "UserControl", "TemplatedControl", "ContentControl", "ItemsControl", "Window", "Panel", "Decorator", "Border", "Grid", "StackPanel", "DockPanel", "WrapPanel", "Canvas", "Button", "ToggleButton", "ListBox", "TextBox", "ScrollViewer", "Viewbox", "ComboBox", "TabControl", "Slider", "ProgressBar", "CheckBox", "RadioButton" };
    const int ViewLineBudget = 800;
    static bool MentionsAvalonia(SyntaxTree tree)
    {
        var root = tree.GetRoot();
        // `global::Avalonia.Controls` is an alias-qualified name, so the prefix is removed before the test.
        static string Plain(SyntaxNode n) => n.ToString().Replace("global::", "");
        return root.DescendantNodes().OfType<UsingDirectiveSyntax>().Any(d => { var n = d.Name == null ? null : Plain(d.Name); return n == "Avalonia" || (n != null && n.StartsWith("Avalonia.", StringComparison.Ordinal)); })
            || root.DescendantNodes().OfType<QualifiedNameSyntax>().Any(q => Plain(q).StartsWith("Avalonia.", StringComparison.Ordinal));
    }
    static bool DerivesFromViewBase(INamedTypeSymbol symbol, bool avaloniaInProject)
    {
        for (var b = symbol.BaseType; b != null; b = b.BaseType)
        {
            if (!ViewBases.Contains(b.Name)) continue;
            if (b.TypeKind == TypeKind.Error) { if (avaloniaInProject) return true; continue; }
            string ns = b.ContainingNamespace?.ToDisplayString() ?? "";
            if (ns == "Avalonia" || ns.StartsWith("Avalonia.", StringComparison.Ordinal)) return true;
        }
        return false;
    }
    static string Member(SyntaxNode n)
    {
        var owner = n.Ancestors().FirstOrDefault(Callable);
        string prefix = owner == null ? Qualified(n) : Member(owner);
        string args(ParameterListSyntax p) => "(" + string.Join(",", p.Parameters.Select(x => (x.Modifiers.ToString() + " " + x.Type?.ToString()).Trim())) + ")";
        string id = n switch {
            MethodDeclarationSyntax m => (m.ExplicitInterfaceSpecifier?.ToString() ?? "") + m.Identifier.ValueText + (m.TypeParameterList == null ? "" : "`" + m.TypeParameterList.Parameters.Count) + args(m.ParameterList),
            ConstructorDeclarationSyntax c => (Modifier(c.Modifiers, SyntaxKind.StaticKeyword) ? ".cctor" : ".ctor") + args(c.ParameterList),
            DestructorDeclarationSyntax d => "Finalize()",
            OperatorDeclarationSyntax o => "operator " + o.OperatorToken.Text + args(o.ParameterList),
            ConversionOperatorDeclarationSyntax c => c.ImplicitOrExplicitKeyword.Text + " operator " + c.Type + args(c.ParameterList),
            LocalFunctionStatementSyntax l => "local:" + l.Identifier.ValueText + (l.TypeParameterList == null ? "" : "`" + l.TypeParameterList.Parameters.Count) + args(l.ParameterList),
            AccessorDeclarationSyntax a => (a.Parent?.Parent is PropertyDeclarationSyntax p ? p.Identifier.ValueText : a.Parent?.Parent is IndexerDeclarationSyntax i ? "this[" + string.Join(",", i.ParameterList.Parameters.Select(p => p.Type?.ToString())) + "]" : a.Parent?.Parent is EventDeclarationSyntax e ? e.Identifier.ValueText : "accessor") + "." + a.Keyword.Text,
            _ => "<global>"
        };
        return prefix + "." + id;
    }
    static int Line(SyntaxTree t, SyntaxNode n) => t.GetLineSpan(n.Span).StartLinePosition.Line + 1;
    static int SpanLines(SyntaxTree t, SyntaxNode n) => t.GetLineSpan(n.Span).EndLinePosition.Line - t.GetLineSpan(n.Span).StartLinePosition.Line + 1;
    static int Physical(string text) => text.Length == 0 ? 0 : Microsoft.CodeAnalysis.Text.SourceText.From(text).Lines.Count - ((text.EndsWith("\n") || text.EndsWith("\r")) ? 1 : 0);
    sealed class Unit { public string Path, Project; public SyntaxTree Tree; public SemanticModel Model; }
    sealed class Entity
    {
        public string project { get; set; }
        public string kind { get; set; }
        public string symbol { get; set; }
        public List<string> locations { get; set; } = new();
        public int line { get; set; }
        public string contentHash { get; set; }
        public Dictionary<string,int> locationLines { get; set; } = new();
        public Dictionary<string,int> values { get; set; } = new();
    }
    sealed class Finding
    {
        public string rule { get; set; }
        public string project { get; set; }
        public string path { get; set; }
        public int line { get; set; }
        public string member { get; set; }
        public string symbol { get; set; }
        public string syntaxHash { get; set; }
        public int count { get; set; } = 1;
        public bool candidate { get; set; }
        public string note { get; set; }
    }
    static string AttributeName(AttributeSyntax a, Unit u)
    {
        var resolved = (u.Model.GetSymbolInfo(a).Symbol as IMethodSymbol)?.ContainingType.Name;
        return (resolved ?? a.Name.ToString().Split('.').Last()).Replace("Attribute", "");
    }
    static string Constant(ExpressionSyntax e, Unit u)
    {
        var v = u.Model.GetConstantValue(e);
        if (!v.HasValue || !(v.Value is string)) throw new Exception(u.Path + ":" + Line(u.Tree,e) + " unresolved attribute constant");
        return (string)v.Value;
    }
    sealed class DiagnosticRequest
    {
        public string rule { get; set; }
        public string project { get; set; }
        public string path { get; set; }
        public int line { get; set; }
        public int column { get; set; }
        public string symbol { get; set; }
        public string stage { get; set; }
        public bool reuse { get; set; }
    }
    sealed class ProjectContext
    {
        public string name { get; set; }
        public string[] files { get; set; }
        public string[] references { get; set; }
        public string[] defines { get; set; }
        public string[] bans { get; set; }
    }
    public static string Diagnostics(string root, string requestsJson, string contextsJson, string[] frameworkReferences)
    {
        var requests = JsonSerializer.Deserialize<DiagnosticRequest[]>(requestsJson);
        var contexts = JsonSerializer.Deserialize<ProjectContext[]>(contextsJson);
        var output = new List<object>();
        foreach (var context in contexts)
        {
            var banned = new HashSet<string>(Directory.GetFiles(Path.Combine(root,"eng/core-health"),"BannedSymbols.*.txt").SelectMany(File.ReadAllLines).Concat(context.bans ?? Array.Empty<string>()), StringComparer.Ordinal);
            var trees = new Dictionary<string,SyntaxTree>(StringComparer.OrdinalIgnoreCase);
            var options = new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse, preprocessorSymbols:context.defines.Where(d => d.Length>0));
            foreach (var file in context.files.Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!File.Exists(Path.Combine(root,file))) throw new Exception("Missing evaluated Compile source " + file);
                trees[file] = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,file)), options, file);
            }
            // The SDK adds this generated file during CoreCompile, after evaluation-only coverage.
            string globals = Path.Combine(root, Path.GetDirectoryName(context.files.FirstOrDefault() ?? "source.cs"), "obj/Release/net10.0/" + context.name + ".GlobalUsings.g.cs");
            var sourceTrees = trees.Values.ToList();
            if (File.Exists(globals)) sourceTrees.Add(CSharpSyntaxTree.ParseText(File.ReadAllText(globals), options));
            else sourceTrees.Add(CSharpSyntaxTree.ParseText("global using System; global using System.Collections.Generic; global using System.IO; global using System.Linq; global using System.Net.Http; global using System.Threading; global using System.Threading.Tasks;", options));
            var refs = frameworkReferences.Concat(context.references).Distinct(StringComparer.OrdinalIgnoreCase).Select(p => MetadataReference.CreateFromFile(p));
            var compilation = CSharpCompilation.Create("HealthDiagnostics_" + context.name, sourceTrees, refs, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe:true));
            foreach (var r in requests.Where(r => r.project == context.name))
            {
                string member, hash, symbol = r.symbol;
                if (!r.path.EndsWith(".cs",StringComparison.OrdinalIgnoreCase)) { member = "MSBuild"; hash = Hash(r.symbol.Trim()); }
                else
                {
                    if (!trees.TryGetValue(r.path, out var tree)) {
                        tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,r.path)), options, r.path);
                        compilation = compilation.AddSyntaxTrees(tree); trees[r.path] = tree;
                    }
                    var syntax = tree.GetRoot(); var text = tree.GetText();
                    if (r.line<1 || r.line>text.Lines.Count || r.column<1) throw new Exception(r.path+":"+r.line+" invalid diagnostic coordinates");
                    var sourceLine = text.Lines[r.line-1];
                    int position = sourceLine.Start+r.column-1;
                    if (position>sourceLine.End) throw new Exception(r.path+":"+r.line+" invalid diagnostic column");
                    var node = syntax.FindToken(Math.Min(position,Math.Max(0,text.Length-1))).Parent;
                    var owner = node.AncestorsAndSelf().FirstOrDefault(Callable);
                    member = owner == null ? Qualified(node) : Member(owner);
                    if (owner == null && node.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault() is PropertyDeclarationSyntax property) member += "." + property.Identifier.ValueText;
                    if (owner == null && node.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault() is FieldDeclarationSyntax field) member += ".fields:" + string.Join(",",field.Declaration.Variables.Select(v => v.Identifier.ValueText));
                    if (member.Length == 0) member = "<global>";
                    var normalized = node.AncestorsAndSelf().FirstOrDefault(n => n is StatementSyntax && n is not BlockSyntax)
                        ?? node.AncestorsAndSelf().FirstOrDefault(n => n is MemberDeclarationSyntax) ?? syntax;
                    hash = Hash(Normal(normalized));
                    if (r.rule == "RS0030")
                    {
                        var model = compilation.GetSemanticModel(tree, ignoreAccessibility:true);
                        ISymbol resolved = null;
                        foreach (var candidate in node.AncestorsAndSelf().TakeWhile(n => n is not StatementSyntax && n is not MemberDeclarationSyntax))
                        {
                            var info = model.GetSymbolInfo(candidate);
                            var symbols = info.Symbol == null ? info.CandidateSymbols : new[]{info.Symbol}.ToImmutableArray();
                            var display = new SymbolDisplayFormat(typeQualificationStyle:SymbolDisplayTypeQualificationStyle.NameOnly,
                                genericsOptions:SymbolDisplayGenericsOptions.IncludeTypeParameters,
                                memberOptions:SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
                                parameterOptions:SymbolDisplayParameterOptions.IncludeType,
                                miscellaneousOptions:SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
                            foreach (var found in symbols)
                            {
                                var method = found as IMethodSymbol;
                                var doc = (method?.ReducedFrom ?? found).OriginalDefinition.GetDocumentationCommentId();
                                bool messageMatch = found.ToDisplayString(display).Replace("?","") == r.symbol.Replace("?","");
                                if (doc != null && banned.Contains(doc) && (info.Symbol != null || symbols.Length == 1 || messageMatch)) { resolved=found; symbol=doc; break; }
                                var type = found as INamedTypeSymbol ?? found.ContainingType;
                                if (type?.GetDocumentationCommentId() is string typeDoc && banned.Contains(typeDoc) && (info.Symbol != null || messageMatch)) { resolved=found; symbol=typeDoc; break; }
                            }
                            if (resolved != null) break;
                        }
                        if (resolved == null && !banned.Contains(symbol))
                        {
                            // Compiler SARIF supplies the resolved overload display. A
                            // dependent expression can be incomplete in this syntax host;
                            // match that exact display against the finite banned manifest.
                            var display = new SymbolDisplayFormat(typeQualificationStyle:SymbolDisplayTypeQualificationStyle.NameOnly,
                                genericsOptions:SymbolDisplayGenericsOptions.IncludeTypeParameters,
                                memberOptions:SymbolDisplayMemberOptions.IncludeContainingType | SymbolDisplayMemberOptions.IncludeParameters,
                                parameterOptions:SymbolDisplayParameterOptions.IncludeType,
                                miscellaneousOptions:SymbolDisplayMiscellaneousOptions.UseSpecialTypes | SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);
                            var matches = banned.Select(id => new { id, value=DocumentationCommentId.GetFirstSymbolForDeclarationId(id,compilation) })
                                .Where(x => x.value != null && x.value.ToDisplayString(display).Replace("?","") == r.symbol.Replace("?","")).ToArray();
                            if (matches.Length == 1) { resolved=matches[0].value; symbol=matches[0].id; }
                        }
                        if (resolved == null && !banned.Contains(symbol)) throw new Exception(r.path+":"+r.line+" cannot resolve banned symbol from '"+r.symbol+"'");
                    }
                }
                output.Add(new { r.rule, r.project, r.path, r.line, r.column, member, symbol, syntaxHash=hash, count=1, r.stage, r.reuse });
            }
        }
        if (output.Count != requests.Length) throw new Exception("Diagnostic project coverage mismatch");
        // A pragma's text can remain unchanged while its suppressed span grows.
        // Record covered statements and expressions, so narrowing/removal lowers debt and
        // adding or substituting covered code cannot offset another removal.
        foreach (var context in contexts)
        foreach (var file in context.files.Distinct(StringComparer.OrdinalIgnoreCase))
        {
            var tree = CSharpSyntaxTree.ParseText(File.ReadAllText(Path.Combine(root,file)), new CSharpParseOptions(LanguageVersion.Latest, preprocessorSymbols:context.defines.Where(d => d.Length>0)), file);
            var syntax = tree.GetRoot();
            var directives = syntax.DescendantTrivia(descendIntoTrivia:true).Select(t => t.GetStructure()).OfType<PragmaWarningDirectiveTriviaSyntax>().Where(p => p.IsActive).OrderBy(p => p.SpanStart).ToArray();
            void Scope(SyntaxNode region, int start, int stop, string id)
            {
                var statements = region.DescendantNodesAndSelf().OfType<StatementSyntax>().Where(s => s is not BlockSyntax && s.SpanStart >= start && s.Span.End <= stop).ToHashSet();
                var expressions = region.DescendantNodesAndSelf().Where(n =>
                    n is ArrowExpressionClauseSyntax ||
                    n is EqualsValueClauseSyntax && (n.Parent is PropertyDeclarationSyntax || n.Parent is VariableDeclaratorSyntax && n.Parent.Parent?.Parent is FieldDeclarationSyntax) ||
                    n is ExpressionSyntax && n.Parent is LambdaExpressionSyntax lambda && lambda.Body == n)
                    .Where(n => n.SpanStart >= start && n.Span.End <= stop && !n.Ancestors().OfType<StatementSyntax>().Any(statements.Contains));
                foreach (var covered in statements.Cast<SyntaxNode>().Concat(expressions))
                {
                    var owner = covered.AncestorsAndSelf().FirstOrDefault(Callable);
                    var member = owner == null ? Qualified(covered) : Member(owner);
                    output.Add(new { rule="suppressionScopes", project=context.name, path=file, line=Line(tree,covered), member=member.Length == 0 ? "<global>" : member, symbol=id, syntaxHash=Hash(Normal(covered)), count=1, stage="suppression", reuse=false });
                }
            }
            foreach (var pragma in directives.Where(p => p.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)))
            {
                var ids = pragma.ErrorCodes.Select(c => c.ToString()).ToArray();
                if (ids.Length == 0) ids = new[]{"ALL"};
                foreach (var id in ids)
                {
                    if (new[]{"VSTHRD100","CS4014","RS0016","RS0017","RS0024","RS0025","RS0036","RS0037","ALL"}.Contains(id)) throw new Exception(file+":"+Line(tree,pragma)+" protected error/all-warning suppression is forbidden");
                    var restore = directives.FirstOrDefault(p => p.SpanStart > pragma.SpanStart && p.DisableOrRestoreKeyword.IsKind(SyntaxKind.RestoreKeyword) && (p.ErrorCodes.Count == 0 || p.ErrorCodes.Any(c => c.ToString()==id)));
                    if (id=="RS0030" && restore==null) throw new Exception(file+":"+Line(tree,pragma)+" whole-file RS0030 disabling is forbidden");
                    Scope(syntax,pragma.Span.End,restore?.SpanStart ?? syntax.FullSpan.End,"pragma:"+id);
                }
            }
            foreach (var attribute in syntax.DescendantNodes().OfType<AttributeSyntax>())
            {
                var name = attribute.Name.ToString().Split('.').Last();
                var alias = syntax.DescendantNodes().OfType<UsingDirectiveSyntax>().FirstOrDefault(u => u.Alias?.Name.Identifier.ValueText == name);
                if (alias != null) name = alias.Name.ToString().Split('.').Last();
                if (name is not ("SuppressMessage" or "SuppressMessageAttribute" or "UnconditionalSuppressMessage" or "UnconditionalSuppressMessageAttribute")) continue;
                var args = attribute.ArgumentList?.Arguments;
                if (args == null || args.Value.Count<2 || args.Value[1].Expression is not LiteralExpressionSyntax code) throw new Exception(file+":"+Line(tree,attribute)+" suppression check ID must be resolved before enrollment");
                var id = code.Token.ValueText.Split(':')[0];
                if (new[]{"VSTHRD100","CS4014","RS0016","RS0017","RS0024","RS0025","RS0036","RS0037"}.Contains(id)) throw new Exception(file+":"+Line(tree,attribute)+" protected error suppression is forbidden");
                var region = attribute.Ancestors().OfType<MemberDeclarationSyntax>().FirstOrDefault() ?? syntax;
                Scope(region,region.SpanStart,region.Span.End,"attribute:"+id);
            }
        }
        return JsonSerializer.Serialize(output);
    }

    public static string Measure(string root, string[] paths, string[] projects, string[] referencePaths)
    {
        var units = new List<Unit>();
        var entities = new List<Entity>();
        var findings = new List<Finding>();
        var types = new Dictionary<string,Entity>(StringComparer.Ordinal);
        var state = new Dictionary<string,HashSet<string>>(StringComparer.Ordinal);
        var typeText = new Dictionary<string,List<string>>(StringComparer.Ordinal);
        var codeViews = new HashSet<string>(StringComparer.Ordinal);
        HashSet<string> avaloniaProjects = null;
        var imports = new Dictionary<string,List<Finding>>(StringComparer.Ordinal);
        var discovery = new List<object>();
        var fakes = new Dictionary<string,List<Finding>>(StringComparer.Ordinal);
        for (int i=0; i<paths.Length; i++)
        {
            if (!paths[i].EndsWith(".cs",StringComparison.OrdinalIgnoreCase)) continue;
            string text = File.ReadAllText(System.IO.Path.Combine(root,paths[i]));
            var tree = CSharpSyntaxTree.ParseText(text, new CSharpParseOptions(LanguageVersion.Latest, DocumentationMode.Parse), paths[i]);
            var errors = tree.GetDiagnostics().Where(d => d.Severity == DiagnosticSeverity.Error).ToArray();
            if (errors.Length != 0) throw new Exception(string.Join("\n", errors.Select(d => paths[i] + ":" + (d.Location.GetLineSpan().StartLinePosition.Line+1) + " parse error " + d.Id + ": " + d.GetMessage())));
            units.Add(new Unit { Path=paths[i], Project=projects[i], Tree=tree });
        }
        var references = referencePaths.Select(p => MetadataReference.CreateFromFile(p)).ToArray();
        foreach (var group in units.GroupBy(u => u.Project))
        {
            var compilation = CSharpCompilation.Create("Health_" + group.Key, group.Select(u => u.Tree), references, new CSharpCompilationOptions(OutputKind.DynamicallyLinkedLibrary, allowUnsafe:true));
            foreach (var u in group) u.Model = compilation.GetSemanticModel(u.Tree, ignoreAccessibility:true);
        }
        foreach (var u in units)
        {
            var syntax = u.Tree.GetRoot();
            var nodes = syntax.DescendantNodes().ToArray();
            var declared = nodes.OfType<BaseTypeDeclarationSyntax>().Select(Qualified).Concat(nodes.OfType<DelegateDeclarationSyntax>().Select(d => Qualified(d) + "." + d.Identifier.ValueText + (d.TypeParameterList == null ? "" : "`" + d.TypeParameterList.Parameters.Count))).Distinct().OrderBy(x => x,StringComparer.Ordinal);
            int physical = Physical(u.Tree.GetText().ToString());
            string fileSymbol = string.Join(";",declared);
            var file = new Entity { project=u.Project, kind="file", symbol="file:" + (fileSymbol.Length==0 ? u.Path : fileSymbol), locations=new(){u.Path}, line=1, contentHash=Hash(u.Tree.GetText().ToString()), values=new(){{"fileLines",physical}} };
            if (u.Path.EndsWith(".axaml.cs",StringComparison.OrdinalIgnoreCase)) file.values["axamlCodeBehindLines"] = physical;
            entities.Add(file);
            void Find(string rule, SyntaxNode node, string symbol, string fingerprint=null, bool candidate=false, string note="")
            {
                if (OwnsSharedHelpers(u.Path, u.Project) && (rule == "fakeClockDuplicates" || rule == "workspaceDuplicates" || rule == "blockingWait")) return;
                var context = node is PragmaWarningDirectiveTriviaSyntax ? syntax.FindToken(node.Span.End).Parent : node;
                var owner = context.AncestorsAndSelf().FirstOrDefault(Callable);
                string responsible = owner == null ? Qualified(context) : Member(owner);
                if (responsible.Length==0) responsible = file.symbol;
                if (owner == null && context.AncestorsAndSelf().OfType<PropertyDeclarationSyntax>().FirstOrDefault() is PropertyDeclarationSyntax property) responsible += "." + property.Identifier.ValueText;
                if (owner == null && context.AncestorsAndSelf().OfType<FieldDeclarationSyntax>().FirstOrDefault() is FieldDeclarationSyntax field) responsible += ".fields:" + string.Join(",",field.Declaration.Variables.Select(v => v.Identifier.ValueText));
                findings.Add(new Finding { rule=rule, project=u.Project, path=u.Path, line=Line(u.Tree,node), member=responsible, symbol=symbol, syntaxHash=Hash(fingerprint ?? Normal(node)), candidate=candidate, note=note });
            }
            foreach (var t in nodes.OfType<BaseTypeDeclarationSyntax>())
            {
                string symbol = Qualified(t), key = u.Project + "|" + symbol;
                if (!types.TryGetValue(key,out var e))
                {
                    e = new Entity { project=u.Project, kind="type", symbol=symbol, line=Line(u.Tree,t), values=new(){{"stateMembers",0},{"partialFiles",0}} };
                    types[key] = e; state[key] = new(StringComparer.Ordinal); typeText[key] = new(); entities.Add(e);
                }
                if (!e.locations.Contains(u.Path)) { e.locations.Add(u.Path); e.locationLines[u.Path] = Line(u.Tree,t); e.values["partialFiles"]++; }
                typeText[key].Add(Normal(t));
                avaloniaProjects ??= units.Where(x => MentionsAvalonia(x.Tree)).Select(x => x.Project).ToHashSet(StringComparer.Ordinal);
                if (u.Model.GetDeclaredSymbol(t) is INamedTypeSymbol viewSymbol && DerivesFromViewBase(viewSymbol, avaloniaProjects.Contains(u.Project))) codeViews.Add(key);
                // Sum declaration spans, not whole files or nested sibling types.
                e.values["viewTypeLines"] = e.values.GetValueOrDefault("viewTypeLines") + SpanLines(u.Tree,t);
                foreach (var f in t.ChildNodes().OfType<FieldDeclarationSyntax>())
                {
                    if (Modifier(f.Modifiers,SyntaxKind.ReadOnlyKeyword) || Modifier(f.Modifiers,SyntaxKind.ConstKeyword)) continue;
                    foreach (var v in f.Declaration.Variables)
                    {
                        state[key].Add("field:" + v.Identifier.ValueText);
                        string type = f.Declaration.Type.ToString().Replace("global::", "").Replace("System.", "");
                        string name = v.Identifier.ValueText;
                        if (new[]{"int","long","uint","ulong","Int32","Int64"}.Contains(type) && new[]{"Generation","Revision","RequestId"}.Any(s => name.IndexOf(s,StringComparison.OrdinalIgnoreCase)>=0))
                            Find("generationFields",v,symbol + "." + name,type + " " + name,false,"Increment/adoption semantics require review.");
                    }
                }
                foreach (var p in t.ChildNodes().OfType<PropertyDeclarationSyntax>())
                {
                    if (p.AccessorList != null && p.AccessorList.Accessors.Any(a => a.IsKind(SyntaxKind.SetAccessorDeclaration) || a.IsKind(SyntaxKind.InitAccessorDeclaration)) && (Modifier(p.Modifiers,SyntaxKind.PartialKeyword) || p.AccessorList.Accessors.All(a => a.Body == null && a.ExpressionBody == null)))
                        state[key].Add("property:" + p.ExplicitInterfaceSpecifier?.ToString() + p.Identifier.ValueText);
                }
                if (t is RecordDeclarationSyntax r && r.ParameterList != null && !(r.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) && Modifier(r.Modifiers,SyntaxKind.ReadOnlyKeyword)))
                    foreach (var p in r.ParameterList.Parameters) state[key].Add("property:" + p.Identifier.ValueText);
                string nameT = t.Identifier.ValueText;
                bool provider = t is TypeDeclarationSyntax td && td.BaseList != null && td.BaseList.Types.Any(b => b.Type.ToString().Split('.').Last() == "TimeProvider" || (u.Model.GetTypeInfo(b.Type).Type as INamedTypeSymbol)?.ToDisplayString() == "System.TimeProvider");
                for (var ancestor = (u.Model.GetDeclaredSymbol(t) as INamedTypeSymbol)?.BaseType; ancestor != null; ancestor = ancestor.BaseType)
                    if (ancestor.ToDisplayString()=="System.TimeProvider") provider = true;
                bool knownClock = nameT == "ClockState" || new[]{"Fake","Manual","Test"}.Any(p => nameT.StartsWith(p,StringComparison.Ordinal) && (nameT.EndsWith("Clock",StringComparison.Ordinal) || nameT.EndsWith("TimeProvider",StringComparison.Ordinal)));
                if (provider || knownClock) Find("fakeClockDuplicates",t,symbol,Normal(t),true,"Candidate outside reviewed owner; ClockState is not necessarily a TimeProvider.");
                var ownedCalls = t.DescendantNodes().OfType<InvocationExpressionSyntax>().Where(c => c.Ancestors().OfType<BaseTypeDeclarationSyntax>().FirstOrDefault() == t).ToArray();
                bool directoryCall(string type, string method) => ownedCalls.Any(c => Call(c)==method && c.Expression is MemberAccessExpressionSyntax m && m.Expression.ToString().Split('.').Last()==type);
                bool lifecycle = directoryCall("Path","GetTempPath") && directoryCall("Directory","CreateDirectory") && directoryCall("Directory","Delete");
                if (nameT == "TestWorkspace" || lifecycle) Find("workspaceDuplicates",t,symbol,Normal(t),true,nameT == "TestWorkspace" ? "TestWorkspace implementation." : "Temp-directory lifecycle clone candidate.");
            }
            foreach (var n in nodes.Where(Callable))
            {
                string member = Member(n);
                entities.Add(new Entity { project=u.Project,kind="member",symbol=member,locations=new(){u.Path},line=Line(u.Tree,n),contentHash=Hash(Normal(n)),values=new(){{"methodLines",SpanLines(u.Tree,n)}} });
                SyntaxTokenList mods = n is MethodDeclarationSyntax m ? m.Modifiers : n is LocalFunctionStatementSyntax l ? l.Modifiers : default;
                TypeSyntax ret = n is MethodDeclarationSyntax mm ? mm.ReturnType : n is LocalFunctionStatementSyntax ll ? ll.ReturnType : null;
                if (Modifier(mods,SyntaxKind.AsyncKeyword) && ret is PredefinedTypeSyntax p && p.Keyword.IsKind(SyntaxKind.VoidKeyword))
                {
                    string signature = n is MethodDeclarationSyntax x ? Normal(x.WithBody(null).WithExpressionBody(null).WithSemicolonToken(default)) : Normal(((LocalFunctionStatementSyntax)n).WithBody(null).WithExpressionBody(null).WithSemicolonToken(default));
                    Find("asyncVoid",n,member,signature);
                }
            }
            foreach (var lambda in nodes.OfType<AnonymousFunctionExpressionSyntax>().Where(l => l.AsyncKeyword.IsKind(SyntaxKind.AsyncKeyword)))
            {
                var converted = u.Model.GetTypeInfo(lambda).ConvertedType as INamedTypeSymbol;
                if (converted?.DelegateInvokeMethod?.ReturnsVoid == true || converted?.DelegateInvokeMethod == null)
                    Find("asyncVoid",lambda,"async-lambda:" + (converted?.ToDisplayString() ?? "unresolved-void-delegate"),null,converted?.DelegateInvokeMethod == null,"Unresolved conversions are retained as candidates; H03 resolves project references.");
            }
            foreach (var access in nodes.OfType<MemberAccessExpressionSyntax>().Where(m => Name(m.Name)=="Result")) Find("blockingWait",access,"Result",null,true,"Syntax candidate; domain Result access may need classification.");
            foreach (var access in nodes.OfType<MemberBindingExpressionSyntax>().Where(m => Name(m.Name)=="Result")) Find("blockingWait",access,"Result",null,true,"Conditional Result syntax candidate.");
            var calls = nodes.OfType<InvocationExpressionSyntax>().ToArray();
            bool testFile = IsTestPath(u.Path, u.Project);
            if (testFile)
            {
                bool sanctionedReader = u.Project.Contains("Architecture.Tests",StringComparison.OrdinalIgnoreCase) || new[]{"Architecture","Boundary","Layout","Snapshot"}.Any(s => u.Path.Contains(s,StringComparison.OrdinalIgnoreCase));
                foreach (var c in calls)
                {
                    string name = Call(c);
                    string receiver = c.Expression is MemberAccessExpressionSyntax access ? access.Expression.ToString().Split('.').Last() : "";
                    if ((receiver=="Task" && name=="Delay") || (receiver=="Thread" && name=="Sleep") || (receiver=="SpinWait" && name=="SpinUntil"))
                        Find("timeWaitsInTests",c,receiver + "." + name,null,true,"A real-time wait in a test. Use SignalWait or ManualTimeProvider.");
                    if (receiver=="Assert" && c.DescendantNodes().OfType<IdentifierNameSyntax>().Any(i => i.Identifier.ValueText.Contains("Elapsed",StringComparison.OrdinalIgnoreCase) || i.Identifier.ValueText.Contains("Stopwatch",StringComparison.OrdinalIgnoreCase)))
                        Find("elapsedAssertionsInTests",c,"Assert." + name,null,true,"An assertion on elapsed time. Use ManualTimeProvider and assert on state.");
                    if ((receiver=="Path" && (name=="GetTempPath" || name=="GetTempFileName")) || (receiver=="Directory" && name=="CreateTempSubdirectory"))
                        Find("tempPathInTests",c,receiver + "." + name,null,true,"A temporary path outside TestWorkspace.");
                    if (!sanctionedReader && new[]{"ReadText","ReadAllText","ReadAllTextAsync","ReadAllLines","ReadAllLinesAsync","OpenText"}.Contains(name))
                        Find("sourceTextReadsInTests",c,name,null,true,"A text read in a test outside the Architecture projects; source-text tests belong in one reader.");
                    if (name=="UseHeadless" || (receiver=="HeadlessUnitTestSession" && (name=="StartNew" || name=="GetOrStartForAssembly")))
                        Find("headlessSessionSetups",c,(receiver.Length==0 ? "" : receiver + ".") + name,null,true,"A headless session setup; use the one fixture of the test assembly.");
                    if (receiver=="Process" && name=="Start")
                        Find("childProcessInTests",c,"Process.Start",null,true,"A child process in a test. Use ChildProcessFixture.");
                }
                foreach (var creation in nodes.OfType<ObjectCreationExpressionSyntax>())
                {
                    string created = creation.Type.ToString().Split('.').Last();
                    if (created=="Process" || created=="ProcessStartInfo") Find("childProcessInTests",creation,"new " + created,null,true,"A child process in a test. Use ChildProcessFixture.");
                }
                foreach (var t in nodes.OfType<BaseTypeDeclarationSyntax>().Where(t => IsFakeName(t.Identifier.ValueText) && !Modifier(t.Modifiers,SyntaxKind.PartialKeyword)))
                {
                    string fakeName = t.Identifier.ValueText;
                    if (!fakes.TryGetValue(fakeName,out var participants)) fakes[fakeName] = participants = new();
                    participants.Add(new Finding { rule="sameNameFakes", project=u.Project, path=u.Path, line=Line(u.Tree,t), member=Qualified(t), symbol=fakeName, syntaxHash=Hash(fakeName), candidate=true, note="The same fake name is declared in more than one file; share one type." });
                }
            }
            foreach (var c in calls)
            {
                string name = Call(c);
                if (new[]{"Wait","WaitAll","WaitAny"}.Contains(name) || (name=="GetResult" && c.Expression is MemberAccessExpressionSyntax m && m.Expression is InvocationExpressionSyntax inner && Call(inner)=="GetAwaiter"))
                    Find("blockingWait",c,name,null,true,"Syntax candidate; Task/ValueTask and dedicated-thread approval are H03/review classifications.");
            }
            foreach (var pragma in syntax.DescendantTrivia(descendIntoTrivia:true).Select(t => t.GetStructure()).OfType<PragmaWarningDirectiveTriviaSyntax>().Where(p => p.DisableOrRestoreKeyword.IsKind(SyntaxKind.DisableKeyword)))
            {
                string[] ids = pragma.ErrorCodes.Select(c => c.ToString()).ToArray();
                Find("suppressions",pragma,"pragma:directive", "disable:" + string.Join(",",ids));
                foreach (string id in ids.Length==0 ? new[]{"ALL"} : ids) Find("suppressions",pragma,"pragma:" + id,"disable:" + id);
            }
            foreach (var a in nodes.OfType<AttributeSyntax>())
            {
                string name = AttributeName(a,u);
                if (testFile && name=="AvaloniaTestApplication") Find("headlessSessionSetups",a,"AvaloniaTestApplication",null,true,"A headless session setup; use the one fixture of the test assembly.");
                if (name=="SuppressMessage" || name=="UnconditionalSuppressMessage") {
                    string id = a.ArgumentList?.Arguments.Count >= 2 ? Constant(a.ArgumentList.Arguments[1].Expression,u) : "ALL";
                    Find("suppressions",a,name + ":" + id);
                }
                if (name!="DllImport" && name!="LibraryImport") continue;
                if (a.ArgumentList == null || a.ArgumentList.Arguments.Count==0) throw new Exception(u.Path + ":" + Line(u.Tree,a) + " native import missing DLL");
                var method = a.Ancestors().OfType<MethodDeclarationSyntax>().FirstOrDefault();
                if (method == null) throw new Exception(u.Path + ":" + Line(u.Tree,a) + " native import missing method");
                string dll = Constant(a.ArgumentList.Arguments[0].Expression,u);
                var entry = a.ArgumentList.Arguments.FirstOrDefault(x => x.NameEquals?.Name.Identifier.ValueText == "EntryPoint");
                string ep = entry == null ? method.Identifier.ValueText : Constant(entry.Expression,u);
                string key = dll + "|" + ep;
                if (!imports.ContainsKey(key)) imports[key] = new();
                imports[key].Add(new Finding { rule="nativeImportDuplicates",project=u.Project,path=u.Path,line=Line(u.Tree,a),member=Member(method),symbol=key,syntaxHash=Hash(key),note="Duplicate DLL + effective EntryPoint participant; group metric counts excess declarations." });
            }
            int reads = calls.Count(c => new[]{"ReadText","ReadAllText","ReadAllTextAsync","ReadAllLines","ReadAllLinesAsync","ReadAllBytes","ReadAllBytesAsync","OpenText"}.Contains(Call(c)));
            int assertions = reads==0 ? 0 : calls.Count(c => new[]{"Contains","DoesNotContain","Matches","DoesNotMatch"}.Contains(Call(c)));
            bool filtered = u.Project.Contains("Architecture.Tests",StringComparison.OrdinalIgnoreCase) || (u.Project.Contains("Tests",StringComparison.OrdinalIgnoreCase) && new[]{"Architecture","Boundary","Layout","Snapshot"}.Any(s => u.Path.Contains(s,StringComparison.OrdinalIgnoreCase)));
            if (reads>0) discovery.Add(new { project=u.Project,path=u.Path,readCalls=reads,assertions=assertions,planFilter=filtered });
        }
        // Associate by AXAML x:Class, with adjacent .axaml.cs as a fallback.
        var views = new HashSet<string>(StringComparer.Ordinal);
        foreach (var u in units.Where(u => u.Path.EndsWith(".axaml.cs",StringComparison.OrdinalIgnoreCase)))
            foreach (var t in u.Tree.GetRoot().DescendantNodes().OfType<BaseTypeDeclarationSyntax>().Where(t => t.Identifier.ValueText == Path.GetFileName(u.Path)[..^9] && !t.Ancestors().OfType<BaseTypeDeclarationSyntax>().Any())) views.Add(u.Project + "|" + Qualified(t));
        for (int i=0; i<paths.Length; i++)
        {
            if (!paths[i].EndsWith(".axaml",StringComparison.OrdinalIgnoreCase)) continue;
            var xml = System.Xml.Linq.XDocument.Load(Path.Combine(root,paths[i]));
            string className = xml.Root?.Attributes().FirstOrDefault(a => a.Name.LocalName=="Class" && a.Name.NamespaceName=="http://schemas.microsoft.com/winfx/2006/xaml")?.Value;
            if (className != null) foreach (var e in types.Values.Where(e => e.symbol==className && e.project==projects[i])) views.Add(e.project + "|" + e.symbol);
        }
        foreach (var pair in types)
        {
            var e = pair.Value; e.values["stateMembers"] = state[pair.Key].Count;
            e.contentHash = Hash(string.Join("\n",typeText[pair.Key].OrderBy(s => s,StringComparer.Ordinal)));
            bool oversizedCodeView = codeViews.Contains(pair.Key) && e.values.GetValueOrDefault("viewTypeLines") > ViewLineBudget;
            if (!views.Contains(pair.Key) && !oversizedCodeView) e.values.Remove("viewTypeLines");
        }
        foreach (var group in fakes.Values.Where(g => g.Select(f => f.path).Distinct(StringComparer.Ordinal).Count()>1)) findings.AddRange(group);
        int nativeExcess = 0, nativeGroups = 0;
        foreach (var group in imports.Values.Where(g => g.Count>1)) { nativeGroups++; nativeExcess += group.Count-1; findings.AddRange(group); }
        // Count repeated fingerprints, never collapse unrelated members into totals.
        findings = findings.GroupBy(f => f.rule+"|"+f.project+"|"+f.member+"|"+f.symbol+"|"+f.syntaxHash).Select(g => { var f=g.First(); f.count=g.Count(); return f; }).ToList();
        return JsonSerializer.Serialize(new { entities, findings, sourceTextAssertions=new { candidates=discovery }, nativeImportDuplicates=new { groups=nativeGroups,excess=nativeExcess } });
    }
}
'@

function Measure-Syntax {
    $sdkVersion = Invoke-Checked 'dotnet' @('--version')
    if ($sdkVersion -notmatch '^\d+\.\d+\.\d+[^\r\n]*$') { throw 'Parser cannot be loaded: invalid SDK selection' }
    $sdkLines = (Invoke-Checked 'dotnet' @('--list-sdks')) -split '\r?\n'
    $sdkLine = @($sdkLines | Where-Object { $_.StartsWith("$sdkVersion [", [StringComparison]::Ordinal) })
    if ($sdkLine.Count -ne 1) { throw "Parser cannot be loaded: selected SDK $sdkVersion is not installed" }
    $sdkHome = Join-Path ($sdkLine[0].Substring($sdkVersion.Length + 2).TrimEnd(']')) $sdkVersion
    $parser = Join-Path $sdkHome 'Roslyn/bincore'
    if ($ParserDirectory) {
        if (-not (Test-Path -LiteralPath $ParserDirectory -PathType Container)) { throw "Parser cannot be loaded: missing $ParserDirectory" }
        if ([IO.Path]::GetFullPath($ParserDirectory) -ne [IO.Path]::GetFullPath($parser)) { throw 'Parser cannot be loaded: only the selected SDK parser is permitted' }
    }
    foreach ($assembly in @('Microsoft.CodeAnalysis.dll', 'Microsoft.CodeAnalysis.CSharp.dll', 'csc.dll')) {
        if (-not (Test-Path -LiteralPath (Join-Path $parser $assembly) -PathType Leaf)) { throw "Parser cannot be loaded: missing $parser/$assembly" }
    }
    $major = $sdkVersion.Split('.')[0]
    $packs = Join-Path (Split-Path (Split-Path $sdkHome -Parent) -Parent) 'packs/Microsoft.NETCore.App.Ref'
    $pack = @(Get-ChildItem -LiteralPath $packs -Directory | Where-Object { $_.Name -match "^$major\." } | Sort-Object { [version]$_.Name } -Descending)[0]
    $refs = @(Get-ChildItem -LiteralPath (Join-Path $pack.FullName "ref/net$major.0") -Filter '*.dll' | ForEach-Object { $_.FullName })
    if ($refs.Count -eq 0) { throw 'Parser cannot be loaded: missing SDK reference pack' }
    $csFiles = @($script:Inventory | Where-Object { $_.EndsWith('.cs', [StringComparison]::OrdinalIgnoreCase) })
    if ($csFiles.Count -eq 0) { throw 'No tracked C# input in the measurement root (untracked fixture files must be staged in a Git tree)' }
    $inputFiles = @($csFiles) + @($script:Inventory | Where-Object { $_.EndsWith('.axaml', [StringComparison]::OrdinalIgnoreCase) })
    $projects = @($script:Inventory | Where-Object { $_.EndsWith('.csproj', [StringComparison]::OrdinalIgnoreCase) })
    $projectNames = [System.Collections.Generic.List[string]]::new()
    foreach ($file in $inputFiles) {
        $directory = Split-Path (Join-Path $Root $file) -Parent
        $name = '<root>'
        while ($directory.Length -ge $Root.Length) {
            $near = @($projects | Where-Object { (Split-Path (Join-Path $Root $_) -Parent) -eq $directory })
            if ($near.Count -gt 1) { throw "Ambiguous project ownership for $file; H03 project coverage is required" }
            if ($near.Count -eq 1) { $name = [IO.Path]::GetFileNameWithoutExtension($near[0]); break }
            if ($directory -eq $Root) { break }
            $directory = Split-Path $directory -Parent
        }
        $projectNames.Add($name)
    }
    if ($script:HealthProjects.Count) {
        $evaluatedFiles = [Collections.Generic.List[string]]::new()
        $evaluatedNames = [Collections.Generic.List[string]]::new()
        foreach ($project in $script:HealthProjects) {
            foreach ($item in $project.evaluation.Items.Compile) {
                $path = Health-Path $item.FullPath
                if ($path -cin $csFiles) { $evaluatedFiles.Add($path); $evaluatedNames.Add($project.name) }
            }
        }
        foreach ($path in $csFiles) { if ($path -cnotin $evaluatedFiles) { throw "HC_COVERAGE $path`:1 tracked source has no evaluated Compile owner" } }
        for ($i=0; $i -lt $inputFiles.Count; $i++) { if ($inputFiles[$i].EndsWith('.axaml')) { $evaluatedFiles.Add($inputFiles[$i]); $evaluatedNames.Add($projectNames[$i]) } }
        $inputFiles = $evaluatedFiles.ToArray(); $projectNames = $evaluatedNames
    }
    $temporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
    $temporary = Join-Path $temporaryRoot ('repo-health-' + [guid]::NewGuid().ToString('N'))
    [void][IO.Directory]::CreateDirectory($temporary)
    $context = $null
    try {
        $source = Join-Path $temporary 'Host.cs'
        $dll = Join-Path $temporary 'Host.dll'
        [IO.File]::WriteAllText($source, $hostSource)
        $response = @('-nologo', '-target:library', '-langversion:latest', ('-out:"{0}"' -f $dll))
        $response += @($refs | ForEach-Object { '-r:"{0}"' -f $_ })
        $response += @((('-r:"{0}"' -f (Join-Path $parser 'Microsoft.CodeAnalysis.dll'))), (('-r:"{0}"' -f (Join-Path $parser 'Microsoft.CodeAnalysis.CSharp.dll'))), ('"{0}"' -f $source))
        $rsp = Join-Path $temporary 'host.rsp'
        [IO.File]::WriteAllLines($rsp, [string[]]$response)
        [void](Invoke-Checked 'dotnet' @('exec', (Join-Path $parser 'csc.dll'), "@$rsp"))
        $context = [Runtime.Loader.AssemblyLoadContext]::new('RepoHealth-' + [guid]::NewGuid(), $true)
        [void]$context.LoadFromAssemblyPath((Join-Path $parser 'Microsoft.CodeAnalysis.dll'))
        [void]$context.LoadFromAssemblyPath((Join-Path $parser 'Microsoft.CodeAnalysis.CSharp.dll'))
        $stream = [IO.MemoryStream]::new([IO.File]::ReadAllBytes($dll))
        try { $hostAssembly = $context.LoadFromStream($stream) } finally { $stream.Dispose() }
        $json = $hostAssembly.GetType('HealthSyntaxHost').GetMethod('Measure').Invoke($null, [object[]]@($Root, [string[]]$inputFiles, $projectNames.ToArray(), [string[]]$refs))
        $m = ConvertFrom-Json -InputObject $json -AsHashtable -Depth 100
        if ($script:DiagnosticRequests.Count -or $script:HealthProjects.Count) {
            $contexts = @()
            foreach ($project in $script:HealthProjects) {
                $refsOutput = ConvertFrom-Json -AsHashtable (Invoke-Checked 'dotnet' @('msbuild', $project.path, '-nologo', '-p:NuGetAudit=false', '-verbosity:quiet', '-p:Configuration=Release', '-target:ResolveReferences', '-getItem:ReferencePath'))
                $contexts += @{ name = $project.name; files = @($project.evaluation.Items.Compile | ForEach-Object { Health-Path $_.FullPath }); references = @($refsOutput.Items.ReferencePath | ForEach-Object { $_.FullPath }); defines = @($project.evaluation.Properties.DefineConstants -split ';'); bans = @($(Join-Path $Root "eng/code-health/$($project.layer)/BannedSymbols.txt") | Where-Object { Test-Path -LiteralPath $_ } | ForEach-Object { [IO.File]::ReadAllLines($_) }) }
            }
            $diagnosticJson = $hostAssembly.GetType('HealthSyntaxHost').GetMethod('Diagnostics').Invoke($null, [object[]]@($Root, ([string](ConvertTo-Json -Depth 100 -InputObject $script:DiagnosticRequests.ToArray())), ([string](ConvertTo-Json -Depth 100 -InputObject $contexts)), [string[]]$refs))
            $script:ResolvedDiagnostics = @(ConvertFrom-Json -AsHashtable -Depth 100 $diagnosticJson)
        }

    }
    finally {
        if ($null -ne $context) { $context.Unload() }
        # Only the fresh GUID directory directly under the checked temp root.
        if ([IO.Path]::GetFullPath((Split-Path $temporary -Parent)).TrimEnd([IO.Path]::DirectorySeparatorChar) -eq $temporaryRoot.TrimEnd([IO.Path]::DirectorySeparatorChar)) { [IO.Directory]::Delete($temporary, $true) }
    }
    # Evaluation only, never a build/restore. Imports and conditions are MSBuild's.
    # H03 adds all configuration/TFM combinations and attribution from its map.
    $props = @($script:Inventory | Where-Object { $_.EndsWith('.props', [StringComparison]::OrdinalIgnoreCase) })
    # Always evaluate tracked projects: a NoWarn that lives only in a .csproj must be seen too.
    if ($projects.Count -or $props.Count) {
        $evaluate = if ($projects.Count) { $projects } else { $props }
        $sdkDefaults = Get-SdkDefaultIds
        foreach ($project in $evaluate) {
            $values = ConvertFrom-Json -AsHashtable -InputObject (Invoke-Checked 'dotnet' @('msbuild', (Join-Path $Root $project), '-nologo', '-p:NuGetAudit=false', '-getProperty:NoWarn,WarningsNotAsErrors'))
            foreach ($property in @('NoWarn', 'WarningsNotAsErrors')) {
                foreach ($id in @($values.Properties[$property] -split '[;,\s]+' | Where-Object { $_ -and $_ -notin $sdkDefaults[$property] } | Sort-Object -Unique)) {
                    if ($id -match '\$\(') { throw "Unresolved evaluated $property in $project" }
                    $m.findings += @{ rule = 'suppressions'; project = [IO.Path]::GetFileNameWithoutExtension($project); path = $project; line = 1; member = 'MSBuild'; symbol = "$property`:$id"; syntaxHash = Get-Hash "$property`:$id"; count = 1; candidate = $false; note = 'Evaluated warning exemption from project/props imports.' }
                }
            }
        }
    }
    $summary = @{}
    foreach ($metric in $script:Limits.Keys) {
        $values = @($m.entities | Where-Object { $_.values.Contains($metric) } | ForEach-Object { $_.values[$metric] })
        $summary[$metric] = @{ total = ($values | Measure-Object -Sum).Sum; max = ($values | Measure-Object -Maximum).Maximum; overLimit = @($values | Where-Object { $_ -gt $script:Limits[$metric] }).Count; excess = (@($values | ForEach-Object { [Math]::Max(0, $_ - $script:Limits[$metric]) }) | Measure-Object -Sum).Sum }
    }
    foreach ($rule in $script:SyntaxRules) { $summary[$rule] = (@($m.findings | Where-Object { $_.rule -eq $rule } | ForEach-Object { $_.count }) | Measure-Object -Sum).Sum }
    $summary.nativeImportDuplicates = $m.nativeImportDuplicates
    $summary.methodLines['splitPlanRequired'] = @($m.entities | Where-Object { $_.values.Contains('methodLines') -and $_.values.methodLines -gt 150 }).Count
    $m.sourceTextAssertions['readCalls'] = (@($m.sourceTextAssertions.candidates | ForEach-Object { $_.readCalls }) | Measure-Object -Sum).Sum
    $m.sourceTextAssertions['assertions'] = (@($m.sourceTextAssertions.candidates | ForEach-Object { $_.assertions }) | Measure-Object -Sum).Sum
    $m['summary'] = $summary
    $m['schemaVersion'] = 1
    $m['measurementVersion'] = $script:Version
    $m['snapshotCommit'] = $script:Snapshot
    $m['limits'] = $script:Limits.Clone()
    $m['repo'] = $Repo
    $m['extensions'] = @{ scope = 'syntax only; use Verify with Solution for enforcement'; diagnosticFingerprints = 'SDK Roslyn diagnostic-syntax-v1'; projectCoverage = 'evaluated Compile ownership during enforcement' }
    return $m
}
function Get-Hash([string]$Text) {
    # Identity hashes ignore line endings: a CRLF and an LF checkout of the same source must agree.
    $normalized = $Text.Replace("`r`n", "`n").Replace("`r", "`n")
    return [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($normalized))).ToLowerInvariant()
}

if ($FunctionsOnly) { return }
$script:DiagnosticRequests = [Collections.Generic.List[hashtable]]::new()
$script:ResolvedDiagnostics = @()
$script:HealthProjects = @()
$script:FormatRuns = @()
$exitCode = 2
try {
    if ($Mode -notin @('Measure', 'Verify', 'LowerBaseline', 'Enroll', 'EnrollTestDebt') -or $Repo -notin @('core', 'nfc', 'nfh', 'nfu')) { throw 'Invalid Mode or Repo' }
    $Root = [IO.Path]::TrimEndingDirectorySeparator([IO.Path]::GetFullPath($Root))
    if (-not (Test-Path -LiteralPath $Root -PathType Container)) { throw "Missing root $Root" }
    Push-Location -LiteralPath $Root
    try {
        $global = $Root
        while (-not (Test-Path -LiteralPath (Join-Path $global 'global.json') -PathType Leaf)) {
            $parent = Split-Path $global -Parent
            if (-not $parent -or $parent -eq $global) { throw 'Parser cannot be loaded: global.json is required' }
            $global = $parent
        }
        $script:GitRoot = ''
        $gitProbe = @(& git -C $Root rev-parse --show-toplevel 2>&1)
        if ($LASTEXITCODE -eq 0) { $script:GitRoot = [IO.Path]::GetFullPath(($gitProbe -join '').Trim()) }
        $script:Snapshot = if ($script:GitRoot) { Invoke-Checked 'git' @('-C', $Root, 'rev-parse', 'HEAD') } else { 'fixture' }
        if ($script:GitRoot) {
            $script:Inventory = @(Get-TrackedInventory $script:GitRoot)
        }
        else { $script:Inventory = @(Get-ChildItem -LiteralPath $Root -File -Recurse -Force | ForEach-Object { [IO.Path]::GetRelativePath($Root, $_.FullName).Replace('\', '/') }) }
        $script:Inventory = @($script:Inventory | Where-Object { -not (@($_ -split '/' | Where-Object { $_ -in @('bin', 'obj', 'artifacts', '.git') }).Count) } | Sort-Object -Unique)
        foreach ($path in $script:Inventory) { if (-not (Test-Path -LiteralPath (Join-Path $Root $path) -PathType Leaf)) { throw "Missing tracked file $path" } }
        $script:ChangedPaths = @()
        $script:Failures = 0
        $baseline = $null
        if ($Mode -eq 'Enroll' -and (Test-Path -LiteralPath (Join-Path $Root $BaselinePath))) { throw 'Enroll refuses: baseline already exists' }
        if ($Mode -notin @('Measure', 'Enroll', 'EnrollTestDebt')) {
            if (-not $script:GitRoot) { throw 'Verify/LowerBaseline require a Git repository with a committed baseline' }
            $baselineFull = [IO.Path]::GetFullPath((Join-Path $Root $BaselinePath))
            if (-not $baselineFull.StartsWith($script:GitRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'BaselinePath must be within the Git repository' }
            $baselineGit = [IO.Path]::GetRelativePath($script:GitRoot, $baselineFull).Replace('\','/')
            $allowedCommit = if ($BaseRef.Trim()) { Invoke-Checked 'git' @('-C', $script:GitRoot, 'merge-base', 'HEAD', $BaseRef) } else { $script:Snapshot }
            $allowed = Read-Baseline (Invoke-Checked 'git' @('-C', $script:GitRoot, 'show', "${allowedCommit}:$baselineGit")) "${allowedCommit}:$baselineGit"
            $baseline = Read-Baseline ([IO.File]::ReadAllText($baselineFull)) $BaselinePath
            $script:ChangedPaths = @((Invoke-Checked 'git' @('-C', $Root, 'diff', '--name-only', '--relative', $allowedCommit, '--')) -split '\r?\n')
            Compare-Debt $allowed $baseline 'ceilings' $false
            $testDebtFull = [IO.Path]::GetFullPath((Join-Path $Root $TestDebtPath))
            if (-not $testDebtFull.StartsWith($script:GitRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'TestDebtPath must be within the Git repository' }
            $testDebtGit = [IO.Path]::GetRelativePath($script:GitRoot, $testDebtFull).Replace('\','/')
            $allowedDebt = $null
            & git -C $script:GitRoot cat-file -e "${allowedCommit}:$testDebtGit" 2>&1 | Out-Null
            if ($LASTEXITCODE -eq 0) { $allowedDebt = Read-TestDebt (Invoke-Checked 'git' @('-C', $script:GitRoot, 'show', "${allowedCommit}:$testDebtGit")) "${allowedCommit}:$testDebtGit" }
            $testDebt = if (Test-Path -LiteralPath $testDebtFull -PathType Leaf) { Read-TestDebt ([IO.File]::ReadAllText($testDebtFull)) $TestDebtPath } else { $null }
            Compare-TestDebtLedger $allowedDebt $testDebt
            if ($Mode -eq 'Verify') {
                Test-RemoveByExpiry $baseline $BaselinePath
                if ($null -ne $testDebt) { Test-RemoveByExpiry $testDebt $TestDebtPath }
            }

        }
        # H03 provider initialization (fixture tests substitute providers here).
        if ($Mode -notin @('Measure', 'EnrollTestDebt')) { Initialize-Enforcement $baseline }
        # With -Solution, EnrollTestDebt attributes files to projects the way Verify does (evaluated Compile items, so a linked file counts for each project). It evaluates only; it does not build.
        if ($Mode -eq 'EnrollTestDebt' -and $Solution) { $script:HealthProjects = Get-ProjectCoverage $null }
        $measurement = if ($Mode -in @('Measure', 'EnrollTestDebt')) { Measure-Syntax } else { Measure-Health }
        if ($Mode -ne 'Measure') { Split-TestFindings $measurement }
        if ($Mode -eq 'Measure') {
            $json = ConvertTo-Json -InputObject $measurement -Depth 100
            if ($OutputPath) { [IO.File]::WriteAllText([IO.Path]::GetFullPath($OutputPath), $json + [Environment]::NewLine) } else { Write-Output $json }
            $exitCode = 0
        }
        elseif ($Mode -eq 'EnrollTestDebt') {
            if (-not $script:GitRoot) { throw 'EnrollTestDebt requires a Git repository' }
            Enroll-TestDebt $measurement ([IO.Path]::GetFullPath((Join-Path $Root $TestDebtPath)))
            $exitCode = 0
        }
        elseif ($Mode -eq 'Enroll') {
            if (-not $script:GitRoot) { throw 'Enroll requires a Git repository' }
            if ($script:Failures) { throw 'Enrollment provider failures' }
            Enroll-Baseline $measurement ([IO.Path]::GetFullPath((Join-Path $Root $BaselinePath)))
            $exitCode = 0
        }
        else {
            Compare-Debt $baseline $measurement 'values' ($Mode -eq 'Verify')
            Compare-TestDebtMeasurement $testDebt $measurement ($Mode -eq 'Verify')
            if ($script:Failures -gt 0) { $exitCode = 1 }
            elseif ($Mode -eq 'LowerBaseline') {
                $matches = Match-Entities $baseline.entities $measurement.entities
                $reduced = @()
                for ($i=0; $i -lt $baseline.entities.Count; $i++) {
                    $e = $baseline.entities[$i]
                    $idx = @($matches.Keys | Where-Object { $matches[$_] -eq $i })
                    foreach ($metric in @($e.ceilings.Keys)) {
                        $value = if ($idx.Count -eq 1 -and $measurement.entities[$idx[0]].values.Contains($metric)) { $measurement.entities[$idx[0]].values[$metric] } else { 0 }
                        if ($value -le (Get-Limit $baseline $metric)) { $e.ceilings.Remove($metric) } else { $e.ceilings[$metric] = $value }
                    }
                    if ($e.ceilings.Count) { $reduced += $e }
                }
                $baseline.entities = $reduced
                $remaining = @{}
                foreach ($f in $measurement.findings) { $remaining[(Finding-Key $f)] = $f.count }
                $baseline.findings = @($baseline.findings | Where-Object { $remaining.ContainsKey((Finding-Key $_)) } | ForEach-Object { $_.count = $remaining[(Finding-Key $_)]; $_ })
                [IO.File]::WriteAllText($baselineFull, (ConvertTo-Json -InputObject $baseline -Depth 100) + [Environment]::NewLine)
                Write-LedgerWarningIds $baseline
                Lower-TestDebt $testDebt $measurement $testDebtFull
                Write-Output 'HC_STATE baseline:1 LowerBaseline passed; only existing debt was deleted or lowered.'
                $exitCode = 0
            }
            else { Write-Output 'HC_STATE syntax:1 Verify passed (syntax, build, SARIF, format, provenance and evaluated project coverage).'; $exitCode = 0 }
        }
    }
    finally { Pop-Location }
}
catch {
    Write-Output "HC_DIAGNOSTIC repo-health:1 old=n/a new=n/a entity=tool/input; $($_.Exception.GetBaseException().Message)"
    Write-Output 'Contributing change: checker input/toolchain. Tool or input error; exit 2.'
    $exitCode = 2
}
exit $exitCode
