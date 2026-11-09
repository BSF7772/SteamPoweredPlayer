# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Builds the phone app and installs it on the phone plugged in by USB, then starts it. Afterwards the cable can go.

.DESCRIPTION
  What Settings > Phone app runs. Looks for the phone first, so a missing cable fails in a second instead of after a build. Builds for the phone's
  own CPU only, in the optimised `phone` build type, and updates with `adb install -r`: the app's data stays, including saved server passwords. It
  never uninstalls: when Android refuses the update, it says what to do.

  Needs USB debugging on (Developer options) and the phone's "Allow USB debugging" prompt accepted once. Exit codes: 0 done, 2 no usable phone,
  3 build failed, 4 install failed.

.PARAMETER Serial     One device, for when several are attached. Also the only way to reach an emulator or a phone on wireless adb.
.PARAMETER IdSuffix   Added to the app's id, so a test install stays apart from the real app (tests on the emulator use this).
.PARAMETER CheckOnly  Stop after looking for the phone.
.PARAMETER NoBuild    Install the APK from the last build.
#>
[CmdletBinding()]
param(
    [string]$Serial,
    [string]$IdSuffix = '',
    [switch]$CheckOnly,
    [switch]$NoBuild
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1') 6>$null
$ErrorActionPreference = 'Continue'     # Windows PowerShell turns a native tool's stderr into errors; every step below checks its own result

$root = Split-Path -Parent $PSScriptRoot
$package = 'app.steampoweredplayer' + $IdSuffix

function Say([string]$text) { [Console]::Out.WriteLine($text); [Console]::Out.Flush() }
function Fail([int]$code, [string]$text) { Say $text; exit $code }

if (-not (Get-Command adb -ErrorAction SilentlyContinue)) { Fail 2 'adb is missing. Run tools\bootstrap.ps1 -Android first.' }
if (-not $env:JAVA_HOME -or -not $env:ANDROID_HOME) { Fail 3 'The Android toolchain is missing. Run tools\bootstrap.ps1 -Android first.' }

# ---- the phone ------------------------------------------------------------------------------------------------------
Say 'Looking for the phone...'
& adb start-server 2>$null | Out-Null
$rows = @(& adb devices -l 2>$null | Select-Object -Skip 1 | Where-Object { $_.Trim() -ne '' })
if ($Serial) {
    $rows = @($rows | Where-Object { ($_ -split '\s+')[0] -eq $Serial })
} else {
    # USB only: the first install has to be over the cable, and an emulator isn't the phone.
    $rows = @($rows | Where-Object { $_ -notmatch '^emulator-' -and $_ -notmatch '^\S+:\d+\s' })
}
if ($rows.Count -eq 0) { Fail 2 'No phone found. Plug it in with a USB cable and turn on USB debugging (Settings > Developer options).' }
if ($rows.Count -gt 1) { Fail 2 ('{0} phones are plugged in. Leave one, or pass -Serial.' -f $rows.Count) }
$fields = $rows[0] -split '\s+'
$device = $fields[0]
switch ($fields[1]) {
    'device' { }
    'unauthorized' { Fail 2 'The phone is asking for permission. Tap "Allow" on its USB debugging prompt, then try again.' }
    default { Fail 2 ('The phone is {0}. Unplug it, plug it back in and try again.' -f $fields[1]) }
}
$abi = (& adb -s $device shell getprop ro.product.cpu.abi 2>$null | Out-String).Trim()
if ($abi -notin @('arm64-v8a', 'x86_64')) { Fail 2 ("This app has no build for the phone's CPU ({0})." -f $abi) }
$model = (& adb -s $device shell getprop ro.product.model 2>$null | Out-String).Trim()
Say ('Found {0} ({1}).' -f $model, $abi)
if ($CheckOnly) { exit 0 }

# ---- build ----------------------------------------------------------------------------------------------------------
$apk = Join-Path $root 'android\app\build\outputs\apk\phone\app-phone.apk'
if (-not $NoBuild) {
    Say 'Building the app...'
    Push-Location (Join-Path $root 'android')
    try {
        $gradle = @('assemblePhone', "-PsppAbi=$abi", '--console=plain', '-q')
        if ($IdSuffix) { $gradle += "-PsppIdSuffix=$IdSuffix" }
        & (Join-Path $root 'android\gradlew.bat') @gradle 2>&1 | ForEach-Object { if ("$_" -match 'error|FAILED|failure') { Say "$_" } }
        if ($LASTEXITCODE -ne 0) { Fail 3 'The build failed; the lines above say why.' }
    } finally { Pop-Location }
}
if (-not (Test-Path -LiteralPath $apk)) { Fail 3 "No APK at $apk." }
Say ('Built, {0:0} MB.' -f ((Get-Item -LiteralPath $apk).Length / 1MB))

# ---- install --------------------------------------------------------------------------------------------------------
Say 'Installing over USB...'
$result = (& adb -s $device install -r $apk 2>&1 | Out-String)
if ($LASTEXITCODE -ne 0 -or $result -notmatch 'Success') {
    if ($result -match 'INSTALL_FAILED_UPDATE_INCOMPATIBLE|signatures') {
        Fail 4 'The phone has this app signed with another key, so Android refuses the update. Uninstall it on the phone once, then try again (this removes its saved servers, which is why this script never does it).'
    }
    if ($result -match 'INSTALL_FAILED_USER_RESTRICTED|INSTALL_FAILED_ABORTED') { Fail 4 'The phone refused the install. Allow "Install via USB" in Developer options, or accept the prompt on the phone, then try again.' }
    if ($result -match 'INSUFFICIENT_STORAGE') { Fail 4 'The phone is out of storage.' }
    Fail 4 ('The install failed: ' + ($result -split "`n" | Where-Object { $_ -match 'Failure|error' } | Select-Object -First 1))
}

& adb -s $device shell monkey -p $package -c android.intent.category.LAUNCHER 1 2>$null | Out-Null
Say 'Installed and started. You can unplug the phone.'
