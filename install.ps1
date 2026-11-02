#Requires -Version 7.0
<#
.SYNOPSIS
    Installs the published Numen Calculator plugin into Flow Launcher.

.DESCRIPTION
    Copies artifacts/publish into %APPDATA%\FlowLauncher\Plugins\NumenCalculator-<version>
    (spec section 12.3). The plugin version and entry assembly come from the published
    plugin.json. Flow Launcher settings and the rate cache live outside the plugin
    folder and are never touched.
#>
[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$PublishDir = Join-Path $Root 'artifacts/publish'
$PublishedManifest = Join-Path $PublishDir 'plugin.json'

if (-not (Test-Path -LiteralPath $PublishDir -PathType Container)) {
    throw "Publish output not found at '$PublishDir'. Run .\build.ps1 first."
}

if (-not (Test-Path -LiteralPath $PublishedManifest -PathType Leaf)) {
    throw "Publish output is missing plugin.json at '$PublishedManifest'. Run .\build.ps1 first."
}

$manifest = Get-Content -LiteralPath $PublishedManifest -Raw | ConvertFrom-Json

$version = [string]$manifest.Version
if ([string]::IsNullOrWhiteSpace($version)) {
    throw "'$PublishedManifest' has no Version."
}

$executeFileName = [string]$manifest.ExecuteFileName
if ([string]::IsNullOrWhiteSpace($executeFileName)) {
    throw "'$PublishedManifest' has no ExecuteFileName."
}

if (-not (Test-Path -LiteralPath (Join-Path $PublishDir $executeFileName) -PathType Leaf)) {
    throw "Publish output is missing the entry assembly '$executeFileName'. Run .\build.ps1 first."
}

if (-not (Test-Path -LiteralPath (Join-Path $PublishDir 'numen_c.dll') -PathType Leaf)) {
    throw "Publish output is missing numen_c.dll. Run .\build.ps1 first."
}

if ([string]::IsNullOrWhiteSpace($env:APPDATA)) {
    throw 'APPDATA is not set; cannot locate the Flow Launcher plugin directory.'
}

$pluginsDir = Join-Path $env:APPDATA 'FlowLauncher\Plugins'
$targetDir = Join-Path $pluginsDir "NumenCalculator-$version"

New-Item -ItemType Directory -Path $pluginsDir -Force | Out-Null

if (Test-Path -LiteralPath $targetDir) {
    Write-Host "Removing previous installation at $targetDir"
    Remove-Item -LiteralPath $targetDir -Recurse -Force
}

New-Item -ItemType Directory -Path $targetDir -Force | Out-Null
Get-ChildItem -LiteralPath $PublishDir -Force | Copy-Item -Destination $targetDir -Recurse -Force

$installedEntry = Join-Path $targetDir $executeFileName
if (-not (Test-Path -LiteralPath $installedEntry -PathType Leaf)) {
    throw "Installation is incomplete: '$installedEntry' is missing."
}

$installedManifest = Join-Path $targetDir 'plugin.json'
if (-not (Test-Path -LiteralPath $installedManifest -PathType Leaf)) {
    throw "Installation is incomplete: '$installedManifest' is missing."
}

Write-Host ''
Write-Host "Installed Numen Calculator $version to $targetDir" -ForegroundColor Green
Write-Host 'Restart Flow Launcher (or exit it from the tray and start it again) to load the plugin.'
Write-Host 'If an old build of the plugin was already loaded, Flow Launcher must be restarted to reload the DLL.'
