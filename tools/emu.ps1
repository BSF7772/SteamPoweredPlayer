# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  The development Android emulator: check, create, start, wait for boot, stop. Portable, everything stays under .tools\.

.DESCRIPTION
  tools\bootstrap.ps1 -Android -Emulator installs what this needs. Windows Hypervisor Platform must be on (a Windows
  feature, the maintainer's switch: this script only reports it).
    tools\emu.ps1 check     hypervisor and acceleration report
    tools\emu.ps1 create    create the AVD when it is missing
    tools\emu.ps1 start     create when missing, start without a window, wait for the boot to finish
    tools\emu.ps1 stop      shut the running emulator down
    tools\emu.ps1 delete    delete the AVD (the next start makes a fresh one)

.PARAMETER Command  check, create, start, stop or delete.
.PARAMETER Window   Start with a window instead of headless.
.PARAMETER Gpu      Emulator GPU mode (default host).
.PARAMETER BootTimeoutSeconds  How long start waits for the boot to finish.
#>
[CmdletBinding()]
param(
    [Parameter(Position = 0, Mandatory)][ValidateSet('check', 'create', 'start', 'stop', 'delete')][string]$Command,
    [switch]$Window,
    [string]$Gpu = 'host',
    [int]$BootTimeoutSeconds = 300
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'env.ps1')

$avdName = 'spp-api36'
$sdk = $env:ANDROID_HOME
if (-not $sdk) { throw 'Android SDK not found: run tools\bootstrap.ps1 -Android -Emulator' }
$emulator = Join-Path $sdk 'emulator\emulator.exe'
$imageDir = Join-Path $sdk 'system-images\android-36\google_apis\x86_64'

function Assert-Installed {
    if (-not (Test-Path -LiteralPath $emulator)) { throw "emulator not installed: run tools\bootstrap.ps1 -Emulator" }
    if (-not (Test-Path -LiteralPath $imageDir)) { throw "system image not installed: run tools\bootstrap.ps1 -Emulator" }
}

function Test-Avd { Test-Path -LiteralPath (Join-Path $env:ANDROID_AVD_HOME "$avdName.ini") }

function New-Avd {
    Assert-Installed
    New-Item -ItemType Directory -Force -Path $env:ANDROID_AVD_HOME, $env:ANDROID_USER_HOME | Out-Null
    if (Test-Avd) { Write-Host "AVD $avdName is already there"; return }
    # Written directly: avdmanager only accepts packages installed by sdkmanager, and the emulator needs just these two files.
    # A phone-sized, roomy device: recordings are large, and the default data partition is small.
    $avdDir = Join-Path $env:ANDROID_AVD_HOME "$avdName.avd"
    New-Item -ItemType Directory -Force -Path $avdDir | Out-Null
    Set-Content -LiteralPath (Join-Path $avdDir 'config.ini') -Encoding ASCII -Value @(
        'avd.ini.encoding=UTF-8', "AvdId=$avdName", "avd.ini.displayname=$avdName",
        'abi.type=x86_64', 'hw.cpu.arch=x86_64', 'hw.cpu.ncore=4', 'hw.ramSize=6144', 'vm.heapSize=512',
        'image.sysdir.1=system-images\android-36\google_apis\x86_64\',
        'tag.id=google_apis', 'tag.display=Google APIs', 'PlayStore.enabled=no',
        'hw.lcd.width=1080', 'hw.lcd.height=2400', 'hw.lcd.density=420',
        'disk.dataPartition.size=16G', 'hw.sdCard=no',
        'hw.gpu.enabled=yes', 'hw.gpu.mode=host', 'hw.keyboard=yes', 'showDeviceFrame=no')
    Set-Content -LiteralPath (Join-Path $env:ANDROID_AVD_HOME "$avdName.ini") -Encoding ASCII -Value @(
        'avd.ini.encoding=UTF-8', "path=$avdDir", 'target=android-36')
    Write-Host "created AVD $avdName in $env:ANDROID_AVD_HOME"
}

function Get-RunningSerial {
    foreach ($line in (& adb devices)) {
        if ($line -match '^(emulator-\d+)\s+device') { return $Matches[1] }
    }
    return $null
}

switch ($Command) {
    'check' {
        $cs = Get-CimInstance Win32_ComputerSystem
        Write-Host "HypervisorPresent: $($cs.HypervisorPresent)"
        Assert-Installed
        & $emulator -accel-check
        exit $LASTEXITCODE
    }
    'create' { New-Avd }
    'start' {
        New-Avd
        if (Get-RunningSerial) { Write-Host "already running: $(Get-RunningSerial)"; exit 0 }
        $emuArgs = @('-avd', $avdName, '-gpu', $Gpu, '-no-snapshot-save', '-no-boot-anim', '-no-audio')
        if (-not $Window) { $emuArgs += '-no-window' }
        $log = Join-Path $env:ANDROID_USER_HOME 'emulator.log'
        Start-Process -FilePath $emulator -ArgumentList $emuArgs -WindowStyle Hidden -RedirectStandardOutput $log -RedirectStandardError "$log.err" | Out-Null
        # adb wait-for-device blocks on the event; only the short boot-property check repeats, adb has no event for it.
        $deadline = (Get-Date).AddSeconds($BootTimeoutSeconds)
        $boot = Start-Job -ScriptBlock { & adb wait-for-device }
        if (-not (Wait-Job $boot -Timeout $BootTimeoutSeconds)) { Remove-Job $boot -Force; throw "no device after $BootTimeoutSeconds s; see $log" }
        Remove-Job $boot -Force
        while ((& adb shell getprop sys.boot_completed 2>$null) -ne '1') {
            if ((Get-Date) -gt $deadline) { throw "boot did not finish in $BootTimeoutSeconds s; see $log" }
            Start-Sleep -Seconds 2
        }
        Write-Host "booted: $(Get-RunningSerial)"
    }
    'stop' {
        $serial = Get-RunningSerial
        if (-not $serial) { Write-Host 'no emulator running'; exit 0 }
        & adb -s $serial emu kill
    }
    'delete' {
        if (Get-RunningSerial) { throw 'stop the emulator first' }
        if (-not (Test-Avd)) { Write-Host 'no AVD to delete'; exit 0 }
        # The folder holds only emulator state this script created: the app's test data, nothing of the maintainer's.
        Remove-Item -LiteralPath (Join-Path $env:ANDROID_AVD_HOME "$avdName.avd") -Recurse -Force
        Remove-Item -LiteralPath (Join-Path $env:ANDROID_AVD_HOME "$avdName.ini") -Force
    }
}
