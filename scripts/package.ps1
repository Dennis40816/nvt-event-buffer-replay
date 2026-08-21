[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$Commit
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ArtifactsRoot = Join-Path $RepoRoot 'artifacts'
$ReleaseRoot = Join-Path $ArtifactsRoot 'release'
$WorkRoot = Join-Path $ArtifactsRoot 'package-work'
$UiPublishRoot = Join-Path $WorkRoot 'publish-ui'
$CliPublishRoot = Join-Path $WorkRoot 'publish-cli'
$PackageName = "NvtEventBufferReplay-v$Version-win-x64"
$PackageRoot = Join-Path $WorkRoot $PackageName
$ZipPath = Join-Path $ReleaseRoot "$PackageName.zip"
$ChecksumPath = "$ZipPath.sha256"
$UiProjectPath = Join-Path $RepoRoot 'src/Nvt.Replay.Avalonia/Nvt.Replay.Avalonia.csproj'
$CliProjectPath = Join-Path $RepoRoot 'src/Nvt.Replay.Cli/Nvt.Replay.Cli.csproj'
$FfmpegRuntimeRoot = Join-Path $PackageRoot 'tools\ffmpeg'

function Assert-SafeChildPath {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    $FullPath = [IO.Path]::GetFullPath($Path)
    $FullParent = [IO.Path]::GetFullPath($Parent).TrimEnd([IO.Path]::DirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
    if (-not $FullPath.StartsWith($FullParent, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to modify path outside '$FullParent': $FullPath"
    }
}

function Reset-Directory {
    param(
        [Parameter(Mandatory = $true)][string]$Path,
        [Parameter(Mandatory = $true)][string]$Parent
    )

    Assert-SafeChildPath -Path $Path -Parent $Parent
    if (Test-Path -LiteralPath $Path) {
        [IO.Directory]::Delete([IO.Path]::GetFullPath($Path), $true)
    }
    [IO.Directory]::CreateDirectory($Path) | Out-Null
}

function Get-LowerSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw "Version must be SemVer without build metadata; received '$Version'."
}
if ($Commit -notmatch '^[0-9a-f]{40}$') {
    throw "Commit must be a lowercase 40-character Git SHA; received '$Commit'."
}

$PinnedVersion = (Get-Content -LiteralPath (Join-Path $RepoRoot 'VERSION') -Raw).Trim()
if ($PinnedVersion -ne $Version) {
    throw "VERSION contains '$PinnedVersion', but packaging requested '$Version'."
}
$HeadCommit = (git -C $RepoRoot rev-parse HEAD).Trim()
if ($LASTEXITCODE -ne 0 -or $HeadCommit -ne $Commit) {
    throw "Packaging must run from exact source commit '$Commit'; checkout is '$HeadCommit'."
}
$DirtyState = @(git -C $RepoRoot status --porcelain --untracked-files=all)
if ($LASTEXITCODE -ne 0 -or $DirtyState.Count -ne 0) {
    throw 'Packaging requires a clean worktree so every packaged byte belongs to the identified commit.'
}

[IO.Directory]::CreateDirectory($ArtifactsRoot) | Out-Null
Reset-Directory -Path $ReleaseRoot -Parent $ArtifactsRoot
Reset-Directory -Path $WorkRoot -Parent $ArtifactsRoot
[IO.Directory]::CreateDirectory($UiPublishRoot) | Out-Null
[IO.Directory]::CreateDirectory($CliPublishRoot) | Out-Null
[IO.Directory]::CreateDirectory($PackageRoot) | Out-Null

function Publish-Executable([string]$ProjectPath, [string]$OutputPath) {
    $PublishArguments = @(
        'publish', $ProjectPath,
        '--configuration', 'Release',
        '--runtime', 'win-x64',
        '--self-contained', 'true',
        '--no-restore',
        '--output', $OutputPath,
        '-p:PublishSingleFile=true',
        '-p:IncludeNativeLibrariesForSelfExtract=true',
        '-p:DebugType=None',
        '-p:DebugSymbols=false',
        "-p:Version=$Version",
        "-p:SourceRevisionId=$Commit",
        '-p:ContinuousIntegrationBuild=true'
    )
    dotnet @PublishArguments
    if ($LASTEXITCODE -ne 0) { throw "dotnet publish failed: $ProjectPath" }
}

Publish-Executable -ProjectPath $UiProjectPath -OutputPath $UiPublishRoot
Publish-Executable -ProjectPath $CliProjectPath -OutputPath $CliPublishRoot

foreach ($Published in @(
    [ordered]@{ Directory = $UiPublishRoot; Name = 'Nvt.Replay.Avalonia.exe' },
    [ordered]@{ Directory = $CliPublishRoot; Name = 'Nvt.Replay.Cli.exe' }
)) {
    $UnexpectedFiles = @(Get-ChildItem -LiteralPath $Published.Directory -File | Where-Object { $_.Name -ne $Published.Name -and $_.Extension -ne '.pdb' })
    if ($UnexpectedFiles.Count -ne 0) { throw "Single-file publish produced unexpected package candidates: $($UnexpectedFiles.Name -join ', ')" }
    if (-not (Test-Path -LiteralPath (Join-Path $Published.Directory $Published.Name) -PathType Leaf)) { throw "Single-file publish did not produce $($Published.Name)." }
}

Copy-Item -LiteralPath (Join-Path $UiPublishRoot 'Nvt.Replay.Avalonia.exe') -Destination (Join-Path $PackageRoot 'NvtEventBufferReplay.exe')
Copy-Item -LiteralPath (Join-Path $CliPublishRoot 'Nvt.Replay.Cli.exe') -Destination (Join-Path $PackageRoot 'nvt-replay.exe')
& (Join-Path $PSScriptRoot 'install-ffmpeg.ps1') -Destination $FfmpegRuntimeRoot | Out-Null
$CommitTime = (git -C $RepoRoot show -s --format=%cI $Commit).Trim()
$ReleaseIdentity = [ordered]@{
    schemaVersion = '1.0'
    product = 'NVT Event Buffer Replay'
    version = $Version
    sourceCommit = $Commit
    sourceCommitTime = $CommitTime
    runtime = 'win-x64'
    selfContained = $true
    offlineDefaults = $true
    telemetry = $false
    payloads = @('NvtEventBufferReplay.exe', 'nvt-replay.exe', 'tools/ffmpeg/bin/ffmpeg.exe', 'tools/ffmpeg/bin/ffprobe.exe')
    bundledFfmpeg = 'tools/ffmpeg/FFMPEG-RUNTIME.json'
}
$IdentityPath = Join-Path $PackageRoot 'RELEASE.json'
$ReleaseIdentity | ConvertTo-Json | Set-Content -LiteralPath $IdentityPath -Encoding utf8NoBOM

$AllowedPackageFiles = @(
    'NvtEventBufferReplay.exe',
    'nvt-replay.exe',
    'RELEASE.json',
    'SHA256SUMS.txt',
    'tools/ffmpeg/FFMPEG-RUNTIME.json',
    'tools/ffmpeg/LICENSE.txt',
    'tools/ffmpeg/NOTICE.txt',
    'tools/ffmpeg/bin/avcodec-62.dll',
    'tools/ffmpeg/bin/avdevice-62.dll',
    'tools/ffmpeg/bin/avfilter-11.dll',
    'tools/ffmpeg/bin/avformat-62.dll',
    'tools/ffmpeg/bin/avutil-60.dll',
    'tools/ffmpeg/bin/ffmpeg.exe',
    'tools/ffmpeg/bin/ffprobe.exe',
    'tools/ffmpeg/bin/swresample-6.dll',
    'tools/ffmpeg/bin/swscale-9.dll'
)
$HashedPackageFiles = $AllowedPackageFiles | Where-Object { $_ -ne 'SHA256SUMS.txt' }
$HashLines = foreach ($Name in $HashedPackageFiles) {
    $Path = Join-Path $PackageRoot $Name
    "$(Get-LowerSha256 -Path $Path)  $Name"
}
$HashLines | Set-Content -LiteralPath (Join-Path $PackageRoot 'SHA256SUMS.txt') -Encoding utf8NoBOM
$ActualPackageFiles = @(Get-ChildItem -LiteralPath $PackageRoot -Recurse -File | ForEach-Object {
    [IO.Path]::GetRelativePath($PackageRoot, $_.FullName).Replace('\', '/')
} | Sort-Object)
if (Compare-Object -ReferenceObject ($AllowedPackageFiles | Sort-Object) -DifferenceObject $ActualPackageFiles) {
    throw 'Release package differs from the closed file allowlist.'
}

Compress-Archive -LiteralPath $PackageRoot -DestinationPath $ZipPath -CompressionLevel Optimal
$ZipHash = Get-LowerSha256 -Path $ZipPath
"$ZipHash  $([IO.Path]::GetFileName($ZipPath))" | Set-Content -LiteralPath $ChecksumPath -Encoding ascii -NoNewline
Write-Output $ZipPath
