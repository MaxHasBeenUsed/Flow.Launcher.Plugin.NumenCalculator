#Requires -Version 7.0
<#
.SYNOPSIS
    Builds numen_c.dll and publishes the Numen Calculator Flow Launcher plugin.

.DESCRIPTION
    Pinned, loud build for spec section 12.3. Verifies the numen submodule commit,
    the .NET 9 SDK, CMake >= 3.21 and the Visual Studio 2022 generator, then
    configures and builds the native C ABI DLL and publishes the plugin to
    artifacts/publish.

.PARAMETER BuildTests
    Also build the native C ABI test targets (configure with -DNUMEN_C_BUILD_TESTS=ON).
#>
[CmdletBinding()]
param(
    [switch]$BuildTests
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$Root = $PSScriptRoot
$NumenCommit = 'd7117da039f201ce0946b847e1d5c9ba31da4694'
$Generator = 'Visual Studio 17 2022'
$MinCMakeVersion = [version]'3.21.0'

$NumenDir = Join-Path $Root 'numen'
$NativeDir = Join-Path $Root 'native'
$NativeBuildDir = Join-Path $NativeDir 'build'
$PluginProject = Join-Path $Root 'src/Flow.Launcher.Plugin.NumenCalculator/Flow.Launcher.Plugin.NumenCalculator.csproj'
$PublishDir = Join-Path $Root 'artifacts/publish'

function Write-Step {
    param([string]$Message)
    Write-Host ''
    Write-Host "==> $Message" -ForegroundColor Cyan
}

function Invoke-Checked {
    param(
        [Parameter(Mandatory)][string]$FilePath,
        [Parameter(Mandatory)][string[]]$Arguments,
        [Parameter(Mandatory)][string]$What
    )

    & $FilePath @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw "$What failed with exit code $LASTEXITCODE`: $FilePath $($Arguments -join ' ')"
    }
}

function Assert-Command {
    param(
        [Parameter(Mandatory)][string]$Name,
        [Parameter(Mandatory)][string]$Hint
    )

    $command = Get-Command -Name $Name -CommandType Application -ErrorAction SilentlyContinue
    if (-not $command) {
        throw "$Name not found on PATH. $Hint"
    }

    return $command.Source
}

Write-Step 'Verifying the pinned numen submodule'

if (-not (Test-Path -LiteralPath $NumenDir -PathType Container)) {
    throw "numen submodule directory is missing at '$NumenDir'. Run 'git submodule update --init --recursive'."
}

Assert-Command -Name 'git' -Hint 'Install Git with submodule support.' | Out-Null

$numenHead = (& git -C $NumenDir rev-parse HEAD)
if ($LASTEXITCODE -ne 0) {
    throw "git -C '$NumenDir' rev-parse HEAD failed with exit code $LASTEXITCODE."
}

$numenHead = ($numenHead | Select-Object -First 1).Trim()
if ($numenHead -ne $NumenCommit) {
    throw @"
numen submodule is at $numenHead but this build is pinned to $NumenCommit.
Fix with:
  git submodule update --init --recursive
  git -C numen checkout $NumenCommit
"@
}

if (-not (Test-Path -LiteralPath (Join-Path $NumenDir 'CMakeLists.txt'))) {
    throw "numen submodule is not checked out: '$NumenDir\CMakeLists.txt' is missing. Run 'git submodule update --init --recursive'."
}

Write-Host "numen @ $numenHead"

Write-Step 'Verifying the .NET 9 SDK'

Assert-Command -Name 'dotnet' -Hint 'Install the .NET 9 SDK from https://dotnet.microsoft.com/download.' | Out-Null

$sdkList = @(& dotnet --list-sdks)
if ($LASTEXITCODE -ne 0) {
    throw "dotnet --list-sdks failed with exit code $LASTEXITCODE."
}

$net9Sdks = @($sdkList | Where-Object { $_ -match '^9\.' })
if ($net9Sdks.Count -eq 0) {
    throw "No .NET 9 SDK installed. Found: $($sdkList -join '; ')"
}

Write-Host "found .NET SDK $($net9Sdks[0])"

Write-Step "Verifying CMake >= $MinCMakeVersion and the '$Generator' generator"

Assert-Command -Name 'cmake' -Hint "Install CMake >= $MinCMakeVersion and put it on PATH." | Out-Null

$cmakeVersionLine = (& cmake --version | Select-Object -First 1)
if ($LASTEXITCODE -ne 0) {
    throw "cmake --version failed with exit code $LASTEXITCODE."
}

if ($cmakeVersionLine -notmatch 'version\s+(\d+)\.(\d+)\.(\d+)') {
    throw "Could not parse the CMake version from '$cmakeVersionLine'."
}

$cmakeVersion = [version]::new([int]$Matches[1], [int]$Matches[2], [int]$Matches[3])
if ($cmakeVersion -lt $MinCMakeVersion) {
    throw "CMake $cmakeVersion is too old; $MinCMakeVersion or newer is required."
}

Write-Host "found CMake $cmakeVersion"

$generators = (& cmake --help)
if ($LASTEXITCODE -ne 0) {
    throw "cmake --help failed with exit code $LASTEXITCODE."
}

$hasGenerator = @($generators | Where-Object { $_ -like "*$Generator*" }).Count -gt 0
if (-not $hasGenerator) {
    throw "CMake generator '$Generator' is unavailable. Install Visual Studio 2022 with the 'Desktop development with C++' workload (MSVC v143 >= 19.43 and the Windows 11 SDK)."
}

Write-Host "found generator '$Generator'"

Write-Step 'Configuring the native build'

if (Test-Path -LiteralPath $NativeBuildDir -PathType Container) {
    $cacheFile = Join-Path $NativeBuildDir 'CMakeCache.txt'
    if (Test-Path -LiteralPath $cacheFile) {
        $cachedGeneratorEntry = Select-String -LiteralPath $cacheFile -Pattern '^CMAKE_GENERATOR:INTERNAL=(.+)$' |
            Select-Object -First 1
        if ($cachedGeneratorEntry) {
            $cachedGenerator = $cachedGeneratorEntry.Matches[0].Groups[1].Value.Trim()
            if ($cachedGenerator -ne $Generator) {
                Write-Host "native/build was configured with '$cachedGenerator'; removing the stale build tree."
                Remove-Item -LiteralPath $NativeBuildDir -Recurse -Force
            }
        }
    }
}

$buildTestsValue = if ($BuildTests) { 'ON' } else { 'OFF' }

Invoke-Checked -FilePath 'cmake' -What 'CMake configure' -Arguments @(
    '-S', $NativeDir,
    '-B', $NativeBuildDir,
    '-G', $Generator,
    '-A', 'x64',
    "-DNUMEN_C_BUILD_TESTS=$buildTestsValue"
)

Write-Step 'Building numen_c.dll (Release, x64)'

Invoke-Checked -FilePath 'cmake' -What 'CMake build of numen_c' -Arguments @(
    '--build', $NativeBuildDir,
    '--config', 'Release',
    '--target', 'numen_c'
)

if ($BuildTests) {
    Write-Step 'Building the native test targets'

    Invoke-Checked -FilePath 'cmake' -What 'CMake build of the native tests' -Arguments @(
        '--build', $NativeBuildDir,
        '--config', 'Release'
    )
}

$nativeDll = Join-Path $NativeBuildDir 'Release/numen_c.dll'
if (-not (Test-Path -LiteralPath $nativeDll)) {
    throw "Expected native library was not produced: '$nativeDll'."
}

$nativeDll = (Resolve-Path -LiteralPath $nativeDll).ProviderPath

Write-Step 'Publishing the plugin'

if (Test-Path -LiteralPath $PublishDir) {
    Remove-Item -LiteralPath $PublishDir -Recurse -Force
}

Invoke-Checked -FilePath 'dotnet' -What 'dotnet publish' -Arguments @(
    'publish', $PluginProject,
    '-c', 'Release',
    '-r', 'win-x64',
    '--self-contained', 'false',
    "-p:NativeDllPath=$nativeDll",
    '-o', $PublishDir
)

Write-Step 'Verifying the published artifacts'

$expectedFiles = @(
    'numen_c.dll',
    'plugin.json',
    'Flow.Launcher.Plugin.NumenCalculator.dll',
    'Images/calculator.png',
    'Languages/en.xaml',
    'THIRD-PARTY-NOTICES.md'
)

$missing = @($expectedFiles | Where-Object { -not (Test-Path -LiteralPath (Join-Path $PublishDir $_) -PathType Leaf) })
if ($missing.Count -gt 0) {
    throw "Published output is incomplete; missing: $($missing -join ', ')."
}

$forbidden = Join-Path $PublishDir 'Flow.Launcher.Plugin.dll'
if (Test-Path -LiteralPath $forbidden) {
    throw "Published output contains Flow.Launcher.Plugin.dll; the Flow plugin contract must not be redistributed."
}

Write-Host ''
Write-Host "Build succeeded. Artifacts: $PublishDir" -ForegroundColor Green
Write-Host 'Install with: .\install.ps1'
