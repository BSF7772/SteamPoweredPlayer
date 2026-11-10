# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Builds the Windows installer: SteamPoweredPlayer-<version>-Setup.exe and SHA256SUMS.txt in dist\.

.DESCRIPTION
  1. tools\bootstrap.ps1 -Release (pinned, SHA-256 checked libmpv, ffmpeg and Inno Setup into .tools\; skipped when already there)
  2. dotnet build: self-contained win-x64 Release build into dist\build (the .NET runtime and Windows App SDK go inside, so
     the user needs to install nothing; libmpv, ffmpeg and licenses\ are copied in by tools\Dependencies.targets)
  3. a check that the folder holds what the app needs
  4. Inno Setup (installer\SteamPoweredPlayer.iss) packs that folder into the single Setup.exe
  5. dist\SHA256SUMS.txt
  Used by the release workflow (.github\workflows\release.yml) and by hand:
    powershell -ExecutionPolicy Bypass -File tools\package.ps1 -Version 0.1.0

.PARAMETER Version  The release version, major.minor.patch (for example 0.1.0). Becomes the file and product version.
.PARAMETER SkipBootstrap  Do not run bootstrap (tools already in .tools\).
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)] [ValidatePattern('^\d+\.\d+\.\d+$')] [string]$Version,
    [switch]$SkipBootstrap
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
$dist = Join-Path $root 'dist'

function Step([string]$m) { Write-Host "`n== $m" }
function Run([string]$exe, [string[]]$arguments) {
    & $exe @arguments
    if ($LASTEXITCODE -ne 0) { throw "$exe failed with exit code $LASTEXITCODE" }
}

if (-not $SkipBootstrap) {
    Step 'Pinned tools'
    & (Join-Path $PSScriptRoot 'bootstrap.ps1') -Release
    if ($LASTEXITCODE -ne 0) { throw 'bootstrap failed' }
}

$deps = Get-Content -LiteralPath (Join-Path $PSScriptRoot 'deps.json') -Raw | ConvertFrom-Json
$inno = $deps.entries | Where-Object { $_.id -eq 'innosetup' }
$iscc = Join-Path $root ".tools\innosetup\$($inno.version)\ISCC.exe"
if (-not (Test-Path -LiteralPath $iscc)) { throw "Inno Setup compiler not found: $iscc (run tools\bootstrap.ps1 -Release)" }

Step "Clean dist ($dist)"
if (Test-Path -LiteralPath $dist) { Remove-Item -LiteralPath $dist -Recurse -Force }   # our own build output, gitignored
New-Item -ItemType Directory -Force -Path $dist | Out-Null

Step "Build $Version (self-contained, win-x64)"
# 'dotnet build', not 'dotnet publish': publish of an unpackaged WinUI 3 app leaves out the compiled XAML (*.xbf and the app's
# *.pri) and the app crashes at start (0xC000027B in Microsoft.UI.Xaml.dll). The build output has everything the app runs with;
# self-contained puts the .NET runtime into it. BaseOutputPath is not src\bin: a running dev copy of the app locks the files there.
Run 'dotnet' @('build', 'src\SteamPoweredPlayer.csproj', '-c', 'Release', '-r', 'win-x64', '--self-contained', 'true',
    "-p:Version=$Version", '-p:Platform=x64', '-p:DebugType=none', '-p:DebugSymbols=false',
    "-p:BaseOutputPath=$(Join-Path $dist 'build')\", '-nologo', '-v', 'q')
$exe = @(Get-ChildItem -LiteralPath (Join-Path $dist 'build') -Recurse -Filter 'SteamPoweredPlayer.exe' | Where-Object { $_.FullName -match '\\win-x64\\' })
if ($exe.Count -ne 1) { throw "expected exactly one SteamPoweredPlayer.exe under dist\build, found $($exe.Count)" }
$app = $exe[0].DirectoryName

Step 'Check the app folder'
$must = @('SteamPoweredPlayer.exe', 'SteamPoweredPlayer.pri', 'App.xbf', 'MainWindow.xbf', 'Views\PlayerPage.xbf', 'coreclr.dll', 'libmpv-2.dll',
    'ffmpeg.exe', 'ffprobe.exe', 'licenses\GPL-3.0.txt', 'licenses\third-party.md')
foreach ($f in $must) {
    if (-not (Test-Path -LiteralPath (Join-Path $app $f))) { throw "missing from the published app: $f" }
}
$files = @(Get-ChildItem -LiteralPath $app -Recurse -File)
$mb = [math]::Round((($files | Measure-Object Length -Sum).Sum) / 1MB)
Write-Host "  $($files.Count) files, $mb MB (they go inside the installer, the user never sees them)"

Step 'Installer'
$isccArgs = @("/DAppVersion=$Version", "/DSourceDir=$app", "/DOutDir=$dist", '/Qp')
if ($env:GITHUB_REPOSITORY) { $isccArgs += "/DRepoUrl=https://github.com/$($env:GITHUB_REPOSITORY)" }   # links in Windows' installed-apps list
Run $iscc ($isccArgs + (Join-Path $root 'installer\SteamPoweredPlayer.iss'))
$setup = Join-Path $dist "SteamPoweredPlayer-$Version-Setup.exe"
if (-not (Test-Path -LiteralPath $setup)) { throw "installer was not produced: $setup" }

Step 'Checksums'
$hash = (Get-FileHash -LiteralPath $setup -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $(Split-Path -Leaf $setup)" | Set-Content -LiteralPath (Join-Path $dist 'SHA256SUMS.txt') -Encoding ascii
Write-Host "  $hash  $(Split-Path -Leaf $setup)  ($([math]::Round((Get-Item $setup).Length / 1MB)) MB)"
Write-Host "`nDone: $setup"
