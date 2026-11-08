# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Downloads a real recording on the emulator with the credentials saved in the debug app, optionally breaks the transfer, and compares every
  file with the source by SHA-256. Reads the share read-only; writes only in the emulator. Pass paths on the command line: nothing real goes in the repo.

.DESCRIPTION
  Needs a running emulator (tools\emu.ps1 start), the debug app installed with a saved server, and a recording big enough to outlast the cut
  (about a minute: `adb emu network speed` doesn't slow SMB here). Fails with exit code 1 when any file differs or is missing.

.PARAMETER Folder     Media folder inside the share, with forward slashes (for example .../video/bg_...).
.PARAMETER ShareRoot  The share's folder on this PC, to compare with.
.PARAMETER Cut        none, network (off for 25 s), kill (the app's process), or unreachable (firewall the host; needs a debuggable emulator image).
.PARAMETER CutAfterSeconds  How long the transfer runs before the cut.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$Folder,
    [Parameter(Mandatory)][string]$ShareRoot,
    [ValidateSet('none', 'network', 'kill', 'unreachable')][string]$Cut = 'none',
    [int]$CutAfterSeconds = 15,
    [int]$TimeoutSeconds = 400
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'env.ps1')

$pkg = 'app.steampoweredplayer'
$receiver = "$pkg/.net.DevDownloadReceiver"
$recording = $Folder.TrimEnd('/').Split('/')[-1]
$source = Join-Path $ShareRoot ($Folder -replace '/', '\')
if (-not (Test-Path -LiteralPath $source -PathType Container)) { throw "source folder not found: $source" }

function Get-ChunkDir {
    $lib = adb shell run-as $pkg ls no_backup/chunks | Where-Object { $_ -match '^[0-9a-f-]{36}$' } | Select-Object -First 1
    "no_backup/chunks/$lib/$recording"
}

function Clear-Download {
    adb shell am force-stop $pkg      # a new process rescans the cache, which is only read from disk when it opens
    $d = Get-ChunkDir
    if ($d -match '/[^/]+/[^/]+$') { adb shell run-as $pkg rm -rf $d }
    adb shell run-as $pkg rm -f no_backup/chunks/cache-index.json
}

function Get-LastLog { adb logcat -d -s SppDownload:* | Select-Object -Last 1 }

function Open-App { adb shell am start -n "$pkg/.MainActivity" | Out-Null }

function Wait-Finished {
    $end = (Get-Date).AddSeconds($TimeoutSeconds)
    while ((Get-Date) -lt $end) {
        $done = adb logcat -d -s SppDownload:I | Select-String 'finished:'
        if ($done) { return ($done | Select-Object -Last 1).Line }
        Start-Sleep -Seconds 2
    }
    throw "no result in $TimeoutSeconds s; last: $(Get-LastLog)"
}

function Show-Transitions {
    $last = ''
    foreach ($l in (adb logcat -d -s SppDownload:*)) {
        if ($l -match '^\S+ (\S+)\s.*SppDownload: (\w+)(:|\s)(.*)$' -and $Matches[2] -ne $last) {
            $last = $Matches[2]
            Write-Host "$($Matches[1]) $last$($Matches[3])$($Matches[4])"
        }
    }
}

Clear-Download
adb logcat -c
Open-App
Start-Sleep -Seconds 2
adb shell am broadcast -n $receiver --es folder $Folder | Out-Null

if ($Cut -ne 'none') {
    Start-Sleep -Seconds $CutAfterSeconds
    Write-Host "before the cut: $(Get-LastLog)"
    switch ($Cut) {
        'network' {
            adb shell svc wifi disable
            adb shell svc data disable
            Start-Sleep -Seconds 25
            adb shell svc wifi enable
            adb shell svc data enable
        }
        'kill' {
            $procId = (adb shell pidof $pkg).Trim()
            adb shell run-as $pkg kill -9 $procId
            Start-Sleep -Seconds 2
            adb logcat -c
            Open-App
        }
        'unreachable' {
            adb root | Out-Null
            Start-Sleep -Seconds 3
            adb wait-for-device
            try {
                adb shell iptables -A OUTPUT -d 10.0.2.2 -j DROP
                Start-Sleep -Seconds 75
                Write-Host "blocked for 75 s: $(Get-LastLog)"
            } finally {
                adb shell iptables -D OUTPUT -d 10.0.2.2 -j DROP
            }
            adb shell svc data disable      # the network event that wakes a download waiting on "can't reach"
            Start-Sleep -Seconds 4
            adb shell svc data enable
        }
    }
}

Write-Host (Wait-Finished)
Show-Transitions

$dir = Get-ChunkDir
$device = @{}
foreach ($line in (adb shell "run-as $pkg sh -c 'cd $dir && sha256sum *.m4s'")) {
    if ($line -match '^([0-9a-f]{64})\s+(\S+)$') { $device[$Matches[2]] = $Matches[1] }
}
$same = 0; $different = 0; $missing = 0
foreach ($f in Get-ChildItem -LiteralPath $source -File -Filter '*.m4s') {
    if (-not $device.ContainsKey($f.Name)) { $missing++; continue }
    if ($device[$f.Name] -eq (Get-FileHash -LiteralPath $f.FullName -Algorithm SHA256).Hash.ToLowerInvariant()) { $same++ }
    else { $different++; Write-Host "DIFFERENT: $($f.Name)" }
}
Write-Host "identical=$same different=$different missing=$missing"
if ($Cut -eq 'unreachable') { adb unroot | Out-Null }
Clear-Download
if ($different -gt 0 -or $missing -gt 0 -or $same -eq 0) { exit 1 }
