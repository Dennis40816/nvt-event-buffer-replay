[CmdletBinding()]
param(
    [ValidateSet('Smoke', 'Full')]
    [string]$Mode = 'Smoke',
    [string]$OutputDirectory = 'artifacts/performance'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'
$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$OutputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) { [IO.Path]::GetFullPath($OutputDirectory) } else { [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDirectory)) }
$ArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'artifacts'))
if (-not ($OutputRoot + [IO.Path]::DirectorySeparatorChar).StartsWith($ArtifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw "Performance output must remain under $ArtifactsRoot"
}
[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$FixtureRoot = Join-Path $OutputRoot 'fixtures'
if (Test-Path -LiteralPath $FixtureRoot) { [IO.Directory]::Delete($FixtureRoot, $true) }
[IO.Directory]::CreateDirectory($FixtureRoot) | Out-Null

$GoldenLine = (Get-Content -LiteralPath (Join-Path $RepoRoot 'tests/fixtures/common-0x83-asil-lifecycle.nds.txt') -TotalCount 1)
$PayloadTokens = @($GoldenLine -split '\s+' | Where-Object { $_ -match '^0x[0-9A-Fa-f]{2}$' })
$Payload = $PayloadTokens[1..($PayloadTokens.Count - 1)] -join ' '

function Write-Records([string]$Path, [int]$Count, [TimeSpan]$Span, [switch]$Heartbeat) {
    $Writer = [IO.StreamWriter]::new($Path, $false, [Text.UTF8Encoding]::new($false), 1MB)
    try {
        $Start = [DateTime]::new(2026, 8, 14, 10, 0, 0, [DateTimeKind]::Local)
        for ($Index = 0; $Index -lt $Count; $Index++) {
            if ($Span -eq [TimeSpan]::Zero) { $Timestamp = $Start } else { $Timestamp = $Start.AddTicks([long]($Span.Ticks * $Index / [Math]::Max(1, $Count - 1))) }
            if ($Heartbeat) {
                $Counter = $Index % 65536
                $Writer.WriteLine("{0:yyyy-MM-dd HH:mm:ss:fff} Read TP 0x99070 2 0x{1:X2} 0x{2:X2}", $Timestamp, (($Counter -shr 8) -band 0xff), ($Counter -band 0xff))
            } else {
                $Writer.WriteLine("{0:yyyy-MM-dd HH:mm:ss:fff} Paint TP 0x01 80 {1}", $Timestamp, $Payload)
            }
        }
    } finally { $Writer.Dispose() }
}

function Write-SizeFixture([string]$Path, [long]$TargetBytes) {
    Write-Records -Path $Path -Count 3 -Span ([TimeSpan]::FromMilliseconds(20))
    $Writer = [IO.StreamWriter]::new($Path, $true, [Text.UTF8Encoding]::new($false), 1MB)
    try {
        $Padding = '#' + ('x' * 8190)
        while ($Writer.BaseStream.Position -lt $TargetBytes) { $Writer.WriteLine($Padding) }
    } finally { $Writer.Dispose() }
}

function Invoke-Benchmark([string]$Path, [int]$SeekSamples, [int]$RenderFrames) {
    $Json = & dotnet run --project (Join-Path $RepoRoot 'src/Nvt.Replay.Cli/Nvt.Replay.Cli.csproj') --configuration Release --no-build -- benchmark $Path --event-buffer-version 0x83 --source-adapter nds-communication-log --sample-seeks $SeekSamples --render-frames $RenderFrames --json
    if ($LASTEXITCODE -ne 0) { throw "Benchmark failed for $Path" }
    return ($Json -join [Environment]::NewLine) | ConvertFrom-Json
}

if ($Mode -eq 'Full') {
    $SizeBytes = 1GB; $PhysicalCount = 1000000; $TimelineCount = 28801; $SeekSamples = 10000; $RenderFrames = 120
    $MemoryLimit = 2048MB; $LoadLimitMs = 300000
} else {
    $SizeBytes = 16MB; $PhysicalCount = 10000; $TimelineCount = 1000; $SeekSamples = 2000; $RenderFrames = 60
    $MemoryLimit = 1024MB; $LoadLimitMs = 60000
}

$SizePath = Join-Path $FixtureRoot 'size.nds.txt'
$PhysicalPath = Join-Path $FixtureRoot 'physical.nds.txt'
$TimelinePath = Join-Path $FixtureRoot 'timeline.nds.txt'
Write-SizeFixture -Path $SizePath -TargetBytes $SizeBytes
Write-Records -Path $PhysicalPath -Count $PhysicalCount -Span ([TimeSpan]::Zero) -Heartbeat
Write-Records -Path $TimelinePath -Count $TimelineCount -Span ([TimeSpan]::FromHours(8))

$SizeReport = Invoke-Benchmark $SizePath 10 3
$PhysicalReport = Invoke-Benchmark $PhysicalPath 0 0
$TimelineReport = Invoke-Benchmark $TimelinePath $SeekSamples $RenderFrames
$Failures = [Collections.Generic.List[string]]::new()
if ($SizeReport.sourceBytes -lt $SizeBytes) { $Failures.Add('size fixture is below target') }
if ($PhysicalReport.physicalRecords -ne $PhysicalCount) { $Failures.Add('physical record count mismatch') }
if ([TimeSpan]$TimelineReport.timelineDuration -lt [TimeSpan]::FromHours(8)) { $Failures.Add('timeline is shorter than eight hours') }
foreach ($Item in @($SizeReport, $PhysicalReport, $TimelineReport)) {
    if ($Item.peakWorkingSetBytes -gt $MemoryLimit) { $Failures.Add("peak memory exceeded for $($Item.sourcePath)") }
    if ($Item.loadMilliseconds -gt $LoadLimitMs) { $Failures.Add("load time exceeded for $($Item.sourcePath)") }
}
if ($TimelineReport.renderedFrames -gt 0 -and $TimelineReport.renderFramesPerSecond -lt 60) { $Failures.Add('Paint rendering fell below 60 FPS') }

$Gate = [ordered]@{
    schemaVersion = '1.0'
    mode = $Mode
    generatedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    thresholds = [ordered]@{ peakWorkingSetBytes = $MemoryLimit; loadMilliseconds = $LoadLimitMs; renderFramesPerSecond = 60 }
    size = $SizeReport
    physicalRecords = $PhysicalReport
    timeline = $TimelineReport
    status = if ($Failures.Count -eq 0) { 'pass' } else { 'fail' }
    failures = @($Failures)
}
$ReportPath = Join-Path $OutputRoot "performance-$($Mode.ToLowerInvariant()).json"
$Gate | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath $ReportPath -Encoding utf8NoBOM
$Gate | ConvertTo-Json -Depth 8
if ($Failures.Count -ne 0) { throw "Performance gate failed: $($Failures -join '; ')" }
