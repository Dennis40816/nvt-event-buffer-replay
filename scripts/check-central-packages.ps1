# Copyright (c) 2026 Dennis Liu. All rights reserved.
#Requires -Version 7.0
[CmdletBinding()]
param([string]$Root = (Split-Path -Parent $PSScriptRoot))

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$Root = [IO.Path]::GetFullPath($Root)

try {
    [xml]$central = [IO.File]::ReadAllText((Join-Path $Root 'Directory.Packages.props'))
    $enabled = @($central.SelectNodes('//*[local-name()="ManagePackageVersionsCentrally"]'))
    if ($enabled.Count -ne 1 -or $enabled[0].InnerText -cne 'true') {
        throw 'HC_CPM Directory.Packages.props:1 central package management must be enabled'
    }
    $projects = @(Get-ChildItem -LiteralPath $Root -Recurse -File -Filter '*.csproj' |
        Where-Object { [IO.Path]::GetRelativePath($Root, $_.FullName) -notmatch '(^|[/\\])(?:bin|obj|artifacts|out|\.git)([/\\]|$)' })
    if (-not $projects.Count) { throw 'HC_CPM repository:1 no C# projects' }
    foreach ($project in $projects) {
        [xml]$document = [IO.File]::ReadAllText($project.FullName)
        $relative = [IO.Path]::GetRelativePath($Root, $project.FullName).Replace('\', '/')
        foreach ($reference in $document.SelectNodes('//*[local-name()="PackageReference"]')) {
            if ($reference.HasAttribute('Version') -or $reference.HasAttribute('VersionOverride') -or
                $reference.SelectNodes('*[local-name()="Version" or local-name()="VersionOverride"]').Count) {
                throw "HC_CPM ${relative}:1 PackageReference must not carry Version or VersionOverride"
            }
        }
        foreach ($setting in $document.SelectNodes('//*[local-name()="ManagePackageVersionsCentrally"]')) {
            if ($setting.InnerText -cne 'true') { throw "HC_CPM ${relative}:1 project disables central package management" }
        }
    }
    Write-Output "Central package management check passed: $($projects.Count) projects."
    exit 0
}
catch {
    Write-Output $_.Exception.Message
    exit 1
}
