# SPDX-License-Identifier: GPL-3.0-or-later
# Dot-source from the repo root:  . tools\env.ps1
# Sets the portable toolchain for THIS shell only (nothing persistent, nothing system-wide). Only sets what exists.
# Versions come from tools\deps.json; the newest installed version under .tools\<id>\ is used. Folders of the Phase 0
# spikes (.tools\jdk, .tools\gradle-9.9.0, .tools\platform-tools, .tools\android\sdk) are accepted as a fallback.

$script:SppRoot = Split-Path -Parent $PSScriptRoot
$script:SppTools = Join-Path $script:SppRoot '.tools'

function script:Get-SppToolDir([string]$id, [string]$probe) {
    $base = Join-Path $script:SppTools $id
    if (Test-Path -LiteralPath $base -PathType Container) {
        $dirs = Get-ChildItem -LiteralPath $base -Directory | Sort-Object Name -Descending
        foreach ($d in $dirs) { if (Test-Path -LiteralPath (Join-Path $d.FullName $probe)) { return $d.FullName } }
    }
    return $null
}

function script:Add-SppPath([string]$dir) {
    if (-not $dir -or -not (Test-Path -LiteralPath $dir -PathType Container)) { return }
    $parts = $env:PATH -split ';'
    if ($parts -notcontains $dir) { $env:PATH = "$dir;$env:PATH" }
}

# JDK
$jdk = script:Get-SppToolDir 'jdk' 'bin\java.exe'
if (-not $jdk -and (Test-Path -LiteralPath (Join-Path $script:SppTools 'jdk\bin\java.exe'))) { $jdk = Join-Path $script:SppTools 'jdk' }
if ($jdk) { $env:JAVA_HOME = $jdk }

# Android SDK root (sdkmanager-managed folder with platforms/, build-tools/ ...)
$sdk = Join-Path $script:SppTools 'android\sdk'
if (Test-Path -LiteralPath $sdk -PathType Container) { $env:ANDROID_HOME = $sdk; $env:ANDROID_SDK_ROOT = $sdk }

# Emulator state stays under .tools\ (the defaults are in the user profile). tools\emu.ps1 creates the folders.
$env:ANDROID_USER_HOME = Join-Path $script:SppTools 'android\user-home'
$env:ANDROID_EMULATOR_HOME = $env:ANDROID_USER_HOME
$env:ANDROID_AVD_HOME = Join-Path $script:SppTools 'android\avd'

# Gradle cache inside the workspace
$gh = Join-Path $script:SppTools 'gradle-home'
if (Test-Path -LiteralPath $gh -PathType Container) { $env:GRADLE_USER_HOME = $gh }

# NuGet packages and .NET CLI home
$nuget = Join-Path $script:SppTools 'nuget'
if (Test-Path -LiteralPath $nuget -PathType Container) { $env:NUGET_PACKAGES = $nuget }
$dn = Join-Path $script:SppTools 'dotnet-home'
if (Test-Path -LiteralPath $dn -PathType Container) { $env:DOTNET_CLI_HOME = $dn }

# PATH: gradle, adb, libmpv, ffmpeg
$gradle = script:Get-SppToolDir 'gradle' 'bin\gradle.bat'
if (-not $gradle -and (Test-Path -LiteralPath (Join-Path $script:SppTools 'gradle-9.9.0\bin'))) { $gradle = Join-Path $script:SppTools 'gradle-9.9.0' }
if ($gradle) { script:Add-SppPath (Join-Path $gradle 'bin') }

$pt = script:Get-SppToolDir 'android-platform-tools' 'adb.exe'
if (-not $pt -and (Test-Path -LiteralPath (Join-Path $script:SppTools 'platform-tools\adb.exe'))) { $pt = Join-Path $script:SppTools 'platform-tools' }
if ($pt) { script:Add-SppPath $pt }

if ($jdk) { script:Add-SppPath (Join-Path $jdk 'bin') }

$mpv = script:Get-SppToolDir 'libmpv' 'libmpv-2.dll'
if ($mpv) { script:Add-SppPath $mpv }

$ff = script:Get-SppToolDir 'ffmpeg' 'ffmpeg.exe'
if ($ff) { script:Add-SppPath $ff }

Write-Host ("SteamPoweredPlayer env: JAVA_HOME={0} ANDROID_HOME={1} libmpv={2} ffmpeg={3}" -f
    $(if ($env:JAVA_HOME) { 'set' } else { '-' }), $(if ($env:ANDROID_HOME) { 'set' } else { '-' }),
    $(if ($mpv) { 'set' } else { '-' }), $(if ($ff) { 'set' } else { 'PATH' }))

Remove-Item -LiteralPath function:Get-SppToolDir, function:Add-SppPath -ErrorAction SilentlyContinue
Remove-Variable jdk, sdk, gh, nuget, dn, gradle, pt, mpv, ff -ErrorAction SilentlyContinue
