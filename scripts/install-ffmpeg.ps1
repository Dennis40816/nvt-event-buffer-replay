[CmdletBinding()]
param(
    [string]$Destination
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$ArtifactsRoot = Join-Path $RepoRoot 'artifacts'
if ([string]::IsNullOrWhiteSpace($Destination)) {
    $Destination = Join-Path $ArtifactsRoot 'ffmpeg-runtime'
}
$Destination = [IO.Path]::GetFullPath($Destination)
$ArtifactsPrefix = [IO.Path]::GetFullPath($ArtifactsRoot).TrimEnd('\') + '\'
if (-not $Destination.StartsWith($ArtifactsPrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw "FFmpeg runtime destination must remain inside '$ArtifactsRoot': $Destination"
}

$DefinitionPath = Join-Path $RepoRoot 'eng\ffmpeg-runtime.json'
$Definition = Get-Content -LiteralPath $DefinitionPath -Raw | ConvertFrom-Json
$CacheRoot = Join-Path $ArtifactsRoot 'ffmpeg-cache'
$ArchivePath = Join-Path $CacheRoot $Definition.assetName
[IO.Directory]::CreateDirectory($CacheRoot) | Out-Null

function Get-LowerSha256([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

if (Test-Path -LiteralPath $ArchivePath) {
    if ((Get-LowerSha256 $ArchivePath) -ne $Definition.assetSha256) {
        [IO.File]::Delete($ArchivePath)
    }
}
if (-not (Test-Path -LiteralPath $ArchivePath)) {
    $TemporaryArchive = "$ArchivePath.$([Guid]::NewGuid().ToString('N')).tmp"
    try {
        Invoke-WebRequest -Uri $Definition.assetUrl -OutFile $TemporaryArchive
        $ActualHash = Get-LowerSha256 $TemporaryArchive
        if ($ActualHash -ne $Definition.assetSha256) {
            throw "FFmpeg archive SHA-256 mismatch: expected $($Definition.assetSha256), received $ActualHash."
        }
        [IO.File]::Move($TemporaryArchive, $ArchivePath)
    }
    finally {
        if (Test-Path -LiteralPath $TemporaryArchive) { [IO.File]::Delete($TemporaryArchive) }
    }
}

if (Test-Path -LiteralPath $Destination) {
    [IO.Directory]::Delete($Destination, $true)
}
$BinDirectory = Join-Path $Destination 'bin'
[IO.Directory]::CreateDirectory($BinDirectory) | Out-Null

$RequiredBinaries = @(
    'ffmpeg.exe',
    'ffprobe.exe',
    'avcodec-62.dll',
    'avdevice-62.dll',
    'avfilter-11.dll',
    'avformat-62.dll',
    'avutil-60.dll',
    'swresample-6.dll',
    'swscale-9.dll'
)

Add-Type -AssemblyName System.IO.Compression
$Archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
try {
    foreach ($Name in $RequiredBinaries) {
        $Entry = @($Archive.Entries | Where-Object { $_.FullName.Replace('\','/').EndsWith("/bin/$Name", [StringComparison]::OrdinalIgnoreCase) })
        if ($Entry.Count -ne 1) { throw "FFmpeg archive must contain exactly one bin/$Name entry." }
        $OutputPath = Join-Path $BinDirectory $Name
        $InputStream = $Entry[0].Open()
        $OutputStream = [IO.File]::Create($OutputPath)
        try { $InputStream.CopyTo($OutputStream) }
        finally { $OutputStream.Dispose(); $InputStream.Dispose() }
    }
    $LicenseEntry = @($Archive.Entries | Where-Object { $_.FullName.Replace('\','/').EndsWith('/LICENSE.txt', [StringComparison]::OrdinalIgnoreCase) })
    if ($LicenseEntry.Count -ne 1) { throw 'FFmpeg archive must contain exactly one LICENSE.txt.' }
    $InputStream = $LicenseEntry[0].Open()
    $OutputStream = [IO.File]::Create((Join-Path $Destination 'LICENSE.txt'))
    try { $InputStream.CopyTo($OutputStream) }
    finally { $OutputStream.Dispose(); $InputStream.Dispose() }
}
finally {
    $Archive.Dispose()
}

$Notice = @"
NVT Event Buffer Replay invokes the separately packaged FFmpeg command-line
program to encode MP4 output. It does not link against FFmpeg libraries.

Build: $($Definition.version)
Provider: $($Definition.provider), release $($Definition.releaseTag)
Archive SHA-256: $($Definition.assetSha256)
License: $($Definition.license) (see LICENSE.txt)
Exact FFmpeg source: $($Definition.ffmpegSourceUrl)
Build scripts/source: $($Definition.buildSourceUrl)
Official project: https://ffmpeg.org/

This package omits ffplay and development headers. It includes ffmpeg,
ffprobe, and the DLLs required by those programs. The reviewed H.264 encoder
order is h264_mf, libopenh264, then libx264 when an operator supplies a
different compatible executable.
"@
$Notice | Set-Content -LiteralPath (Join-Path $Destination 'NOTICE.txt') -Encoding utf8NoBOM

$FileHashes = [ordered]@{}
Get-ChildItem -LiteralPath $Destination -Recurse -File | Sort-Object FullName | ForEach-Object {
    $Relative = [IO.Path]::GetRelativePath($Destination, $_.FullName).Replace('\','/')
    $FileHashes[$Relative] = Get-LowerSha256 $_.FullName
}
$RuntimeIdentity = [ordered]@{
    schemaVersion = '1.0'
    version = $Definition.version
    provider = $Definition.provider
    releaseTag = $Definition.releaseTag
    archiveSha256 = $Definition.assetSha256
    license = $Definition.license
    ffmpegSourceCommit = $Definition.ffmpegSourceCommit
    files = $FileHashes
}
$RuntimeIdentity | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $Destination 'FFMPEG-RUNTIME.json') -Encoding utf8NoBOM

$Ffmpeg = Join-Path $BinDirectory 'ffmpeg.exe'
$Probe = & $Ffmpeg -hide_banner -encoders 2>&1
$ProbeText = $Probe -join [Environment]::NewLine
if ($LASTEXITCODE -ne 0 -or $ProbeText -notmatch '\bh264_mf\b' -or $ProbeText -notmatch '\blibopenh264\b') {
    throw 'Installed FFmpeg runtime did not expose the reviewed h264_mf and libopenh264 encoders.'
}
Write-Output $Destination
