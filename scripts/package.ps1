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
$PublishRoot = Join-Path $WorkRoot 'publish'
$PackageName = "NvtEventBufferReplay-v$Version-win-x64"
$PackageRoot = Join-Path $WorkRoot $PackageName
$ZipPath = Join-Path $ReleaseRoot "$PackageName.zip"
$ChecksumPath = "$ZipPath.sha256"
$ProjectPath = Join-Path $RepoRoot 'src/Nvt.Replay.Avalonia/Nvt.Replay.Avalonia.csproj'

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
[IO.Directory]::CreateDirectory($PublishRoot) | Out-Null
[IO.Directory]::CreateDirectory($PackageRoot) | Out-Null

$PublishArguments = @(
    'publish', $ProjectPath,
    '--configuration', 'Release',
    '--runtime', 'win-x64',
    '--self-contained', 'true',
    '--no-restore',
    '--output', $PublishRoot,
    '-p:PublishSingleFile=true',
    '-p:IncludeNativeLibrariesForSelfExtract=true',
    '-p:DebugType=None',
    '-p:DebugSymbols=false',
    "-p:Version=$Version",
    "-p:SourceRevisionId=$Commit",
    '-p:ContinuousIntegrationBuild=true'
)
dotnet @PublishArguments
if ($LASTEXITCODE -ne 0) {
    throw 'dotnet publish failed.'
}

$PublishedFiles = @(Get-ChildItem -LiteralPath $PublishRoot -File)
$UnexpectedFiles = @($PublishedFiles | Where-Object { $_.Name -ne 'Nvt.Replay.Avalonia.exe' -and $_.Extension -ne '.pdb' })
if ($UnexpectedFiles.Count -ne 0) {
    throw "Single-file publish produced unexpected package candidates: $($UnexpectedFiles.Name -join ', ')"
}
$PublishedExecutable = Join-Path $PublishRoot 'Nvt.Replay.Avalonia.exe'
if (-not (Test-Path -LiteralPath $PublishedExecutable -PathType Leaf)) {
    throw 'Single-file publish did not produce Nvt.Replay.Avalonia.exe.'
}

$PackagedExecutable = Join-Path $PackageRoot 'NvtEventBufferReplay.exe'
Copy-Item -LiteralPath $PublishedExecutable -Destination $PackagedExecutable
$CommitTime = (git -C $RepoRoot show -s --format=%cI $Commit).Trim()
$ReleaseIdentity = [ordered]@{
    schemaVersion = '1.0'
    product = 'NVT Event Buffer Replay'
    version = $Version
    sourceCommit = $Commit
    sourceCommitTime = $CommitTime
    runtime = 'win-x64'
    selfContained = $true
}
$IdentityPath = Join-Path $PackageRoot 'RELEASE.json'
$ReleaseIdentity | ConvertTo-Json | Set-Content -LiteralPath $IdentityPath -Encoding utf8NoBOM

$AllowedPackageFiles = @('NvtEventBufferReplay.exe', 'RELEASE.json', 'SHA256SUMS.txt')
$HashLines = foreach ($Name in @('NvtEventBufferReplay.exe', 'RELEASE.json')) {
    $Path = Join-Path $PackageRoot $Name
    "$(Get-LowerSha256 -Path $Path)  $Name"
}
$HashLines | Set-Content -LiteralPath (Join-Path $PackageRoot 'SHA256SUMS.txt') -Encoding utf8NoBOM
$ActualPackageFiles = @(Get-ChildItem -LiteralPath $PackageRoot -File | ForEach-Object Name | Sort-Object)
if (Compare-Object -ReferenceObject ($AllowedPackageFiles | Sort-Object) -DifferenceObject $ActualPackageFiles) {
    throw 'Release package differs from the closed file allowlist.'
}

Compress-Archive -LiteralPath $PackageRoot -DestinationPath $ZipPath -CompressionLevel Optimal
$ZipHash = Get-LowerSha256 -Path $ZipPath
"$ZipHash  $([IO.Path]::GetFileName($ZipPath))" | Set-Content -LiteralPath $ChecksumPath -Encoding ascii -NoNewline
Write-Output $ZipPath
