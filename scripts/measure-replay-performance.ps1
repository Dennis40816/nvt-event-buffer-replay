[CmdletBinding()]
param(
    [string]$OutputDirectory = 'artifacts/performance',
    [string]$ReportName = 'replay-loop.json'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ArtifactsRoot = [IO.Path]::GetFullPath((Join-Path $RepoRoot 'artifacts'))
$OutputRoot = if ([IO.Path]::IsPathRooted($OutputDirectory)) {
    [IO.Path]::GetFullPath($OutputDirectory)
} else {
    [IO.Path]::GetFullPath((Join-Path $RepoRoot $OutputDirectory))
}
if (-not ($OutputRoot + [IO.Path]::DirectorySeparatorChar).StartsWith(
    $ArtifactsRoot + [IO.Path]::DirectorySeparatorChar,
    [StringComparison]::OrdinalIgnoreCase)) {
    throw "Performance output must remain under $ArtifactsRoot"
}
if ([IO.Path]::GetFileName($ReportName) -ne $ReportName -or
    [IO.Path]::GetExtension($ReportName) -ne '.json') {
    throw 'ReportName must be a JSON file name without directory components.'
}

[IO.Directory]::CreateDirectory($OutputRoot) | Out-Null
$ReportPath = Join-Path $OutputRoot $ReportName
$PreviousReport = $env:NVT_REPLAY_PERF_REPORT
$PreviousCommit = $env:NVT_REPLAY_PERF_COMMIT
try {
    $env:NVT_REPLAY_PERF_REPORT = $ReportPath
    $env:NVT_REPLAY_PERF_COMMIT = (& git -C $RepoRoot rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0) { throw 'Unable to resolve the current git commit.' }

    & dotnet restore (Join-Path $RepoRoot 'Nvt.EventBufferReplay.sln') --locked-mode
    if ($LASTEXITCODE -ne 0) { throw 'Locked restore failed.' }
    & dotnet test (Join-Path $RepoRoot 'tests/Nvt.Replay.Tests/Nvt.Replay.Tests.csproj') `
        --configuration Release `
        --no-restore `
        --filter 'FullyQualifiedName~ReplayLoopPerformanceTests' `
        --logger 'console;verbosity=normal'
    if ($LASTEXITCODE -ne 0) { throw 'Replay loop performance scenario failed.' }
    if (-not (Test-Path -LiteralPath $ReportPath)) { throw "Performance report was not created: $ReportPath" }
    Get-Content -LiteralPath $ReportPath -Raw
} finally {
    $env:NVT_REPLAY_PERF_REPORT = $PreviousReport
    $env:NVT_REPLAY_PERF_COMMIT = $PreviousCommit
}
