[CmdletBinding()]
param(
    [string]$ExpectedTag
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$RepoRoot = [IO.Path]::GetFullPath((Split-Path -Parent $PSScriptRoot))
$VersionPath = Join-Path $RepoRoot 'VERSION'
$ProjectPath = Join-Path $RepoRoot 'src/Nvt.Replay.Core/Nvt.Replay.Core.csproj'
$ManifestPath = Join-Path $RepoRoot 'src/Nvt.Replay.Avalonia/app.manifest'

$Version = (Get-Content -LiteralPath $VersionPath -Raw).Trim()
if ($Version -notmatch '^(?<major>[0-9]+)\.(?<minor>[0-9]+)\.(?<patch>[0-9]+)$') {
    throw "VERSION must contain a stable X.Y.Z identity; received '$Version'."
}

$Tag = "v$Version"
if (-not [string]::IsNullOrWhiteSpace($ExpectedTag) -and $ExpectedTag -ne $Tag) {
    throw "Expected release tag '$ExpectedTag' does not match VERSION-derived tag '$Tag'."
}

$PropertyOutput = dotnet msbuild $ProjectPath -nologo `
    -getProperty:Version `
    -getProperty:AssemblyVersion `
    -getProperty:FileVersion
if ($LASTEXITCODE -ne 0) { throw 'Unable to evaluate .NET release identity.' }
$Properties = ($PropertyOutput -join [Environment]::NewLine) | ConvertFrom-Json
$ExpectedWindowsVersion = "$Version.0"
if ($Properties.Properties.Version -ne $Version) {
    throw "MSBuild Version '$($Properties.Properties.Version)' does not match VERSION '$Version'."
}
if ($Properties.Properties.AssemblyVersion -ne $ExpectedWindowsVersion) {
    throw "AssemblyVersion '$($Properties.Properties.AssemblyVersion)' does not match '$ExpectedWindowsVersion'."
}
if ($Properties.Properties.FileVersion -ne $ExpectedWindowsVersion) {
    throw "FileVersion '$($Properties.Properties.FileVersion)' does not match '$ExpectedWindowsVersion'."
}

[xml]$Manifest = Get-Content -LiteralPath $ManifestPath -Raw
$Namespace = [Xml.XmlNamespaceManager]::new($Manifest.NameTable)
$Namespace.AddNamespace('asm', 'urn:schemas-microsoft-com:asm.v1')
$Identity = $Manifest.SelectSingleNode('/asm:assembly/asm:assemblyIdentity', $Namespace)
if ($null -eq $Identity) { throw 'Windows manifest has no assemblyIdentity.' }
if ($Identity.version -ne $ExpectedWindowsVersion) {
    throw "Windows manifest version '$($Identity.version)' does not match '$ExpectedWindowsVersion'."
}

Write-Output "Release identity verified: version=$Version tag=$Tag windows=$ExpectedWindowsVersion"
