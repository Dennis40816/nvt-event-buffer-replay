[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$PackagePath,

    [switch]$SkipUiLaunch
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

function Get-LowerSha256 {
    param([Parameter(Mandatory = $true)][string]$Path)
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}

$ResolvedPackage = (Resolve-Path -LiteralPath $PackagePath).Path
$ChecksumPath = "$ResolvedPackage.sha256"
if (-not (Test-Path -LiteralPath $ChecksumPath -PathType Leaf)) {
    throw "Outer checksum is missing: $ChecksumPath"
}
$ChecksumParts = (Get-Content -LiteralPath $ChecksumPath -Raw).Trim() -split '\s+', 2
if ($ChecksumParts.Count -ne 2 -or
    $ChecksumParts[1] -ne [IO.Path]::GetFileName($ResolvedPackage) -or
    $ChecksumParts[0] -ne (Get-LowerSha256 -Path $ResolvedPackage)) {
    throw 'Release archive SHA-256 does not match its checksum file.'
}

$SmokeRoot = Join-Path ([IO.Path]::GetTempPath()) "nvt-replay-smoke-$([Guid]::NewGuid().ToString('N'))"
[IO.Directory]::CreateDirectory($SmokeRoot) | Out-Null
try {
    Expand-Archive -LiteralPath $ResolvedPackage -DestinationPath $SmokeRoot
    $PackageDirectories = @(Get-ChildItem -LiteralPath $SmokeRoot -Directory)
    if ($PackageDirectories.Count -ne 1) {
        throw 'Release archive must contain exactly one package directory.'
    }
    $PackageRoot = $PackageDirectories[0].FullName
    $AllowedFiles = @('NvtEventBufferReplay.exe', 'RELEASE.json', 'SHA256SUMS.txt')
    $ActualFiles = @(Get-ChildItem -LiteralPath $PackageRoot -File | ForEach-Object Name | Sort-Object)
    if (Compare-Object -ReferenceObject ($AllowedFiles | Sort-Object) -DifferenceObject $ActualFiles) {
        throw 'Extracted release differs from the closed file allowlist.'
    }

    $InnerChecksumLines = @(Get-Content -LiteralPath (Join-Path $PackageRoot 'SHA256SUMS.txt'))
    if ($InnerChecksumLines.Count -ne 2) {
        throw 'Inner checksum manifest must contain exactly two entries.'
    }
    $InnerChecksumNames = @()
    foreach ($Line in $InnerChecksumLines) {
        $Parts = $Line.Trim() -split '\s+', 2
        if ($Parts.Count -ne 2) {
            throw "Malformed inner checksum line: $Line"
        }
        $Path = Join-Path $PackageRoot $Parts[1]
        if (-not (Test-Path -LiteralPath $Path -PathType Leaf) -or $Parts[0] -ne (Get-LowerSha256 -Path $Path)) {
            throw "Inner SHA-256 mismatch: $($Parts[1])"
        }
        $InnerChecksumNames += $Parts[1]
    }
    if (Compare-Object -ReferenceObject @('NvtEventBufferReplay.exe', 'RELEASE.json') -DifferenceObject ($InnerChecksumNames | Sort-Object)) {
        throw 'Inner checksum manifest differs from the required payload set.'
    }

    $Identity = Get-Content -LiteralPath (Join-Path $PackageRoot 'RELEASE.json') -Raw | ConvertFrom-Json
    if ($Identity.schemaVersion -ne '1.0' -or
        $Identity.version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$' -or
        $Identity.sourceCommit -notmatch '^[0-9a-f]{40}$' -or
        $Identity.runtime -ne 'win-x64' -or
        $Identity.selfContained -ne $true) {
        throw 'Release identity is incomplete or invalid.'
    }

    if (-not $SkipUiLaunch) {
        $Executable = Join-Path $PackageRoot 'NvtEventBufferReplay.exe'
        $Process = Start-Process -FilePath $Executable -PassThru
        try {
            $Deadline = [DateTime]::UtcNow.AddSeconds(15)
            do {
                Start-Sleep -Milliseconds 200
                $Process.Refresh()
            } while (-not $Process.HasExited -and
                [string]::IsNullOrWhiteSpace($Process.MainWindowTitle) -and
                [DateTime]::UtcNow -lt $Deadline)

            if ($Process.HasExited -or [string]::IsNullOrWhiteSpace($Process.MainWindowTitle)) {
                throw 'Packaged UI did not expose a visible main window within 15 seconds.'
            }
        }
        finally {
            if (-not $Process.HasExited) {
                [void]$Process.CloseMainWindow()
                if (-not $Process.WaitForExit(3000)) {
                    $Process.Kill($true)
                }
            }
        }
    }

    Write-Output "Release smoke passed: $($Identity.version) $($Identity.sourceCommit)"
}
finally {
    if (Test-Path -LiteralPath $SmokeRoot) {
        $ResolvedSmokeRoot = [IO.Path]::GetFullPath($SmokeRoot)
        $TemporaryRoot = [IO.Path]::GetFullPath([IO.Path]::GetTempPath())
        if (-not $ResolvedSmokeRoot.StartsWith($TemporaryRoot, [StringComparison]::OrdinalIgnoreCase) -or
            -not [IO.Path]::GetFileName($ResolvedSmokeRoot).StartsWith('nvt-replay-smoke-', [StringComparison]::Ordinal)) {
            throw "Refusing to remove unexpected smoke directory: $ResolvedSmokeRoot"
        }
        [IO.Directory]::Delete($ResolvedSmokeRoot, $true)
    }
}
