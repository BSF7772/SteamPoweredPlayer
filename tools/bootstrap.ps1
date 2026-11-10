# SPDX-License-Identifier: GPL-3.0-or-later
<#
.SYNOPSIS
  Installs the pinned binary dependencies from tools\deps.json into .tools\<id>\<version>\ (portable, no admin, nothing system-wide).

.DESCRIPTION
  Per entry: download into .tools\dl\ (skipped when a file with the right SHA-256 is already there), verify the SHA-256
  (a wrong hash is an error; the file is kept aside as *.bad and never used), extract with the built-in bsdtar, verify the
  hashes of extracted files where the manifest gives them, copy the wanted files to .tools\<id>\<version>\.
  Idempotent: an entry that is already installed and verified is skipped. PowerShell 5.1 compatible.

  Default selection: group 'desktop' without optional entries (libmpv). -IncludeOptional adds ffmpeg, -Android adds the
  Android toolchain, -Emulator adds the Android emulator and its system image (about 2.4 GB to download, not needed to
  build), -All adds everything, -Only <id,...> selects exactly those ids.

  An entry with 'sdkPath' goes into the Android SDK folder (.tools\android\sdk\<sdkPath>) instead of .tools\<id>\<version>\,
  because the emulator finds itself and its images relative to the SDK root.

.PARAMETER Only         Entry ids to install (overrides the group selection).
.PARAMETER IncludeOptional  Also install optional desktop entries (ffmpeg).
.PARAMETER Android      Also install the Android group (jdk, gradle, cmdline-tools, platform-tools).
.PARAMETER Release      Also install what building the Windows installer needs: ffmpeg (bundled in the release) and Inno Setup.
.PARAMETER Emulator     Also install the emulator group (emulator, system image).
.PARAMETER All          Everything, optional entries included.
.PARAMETER Offline      Never download; fail when an archive is missing.
.PARAMETER SeedFrom     Directory with already downloaded archives; a file whose size and SHA-256 match is copied into .tools\dl\.
.PARAMETER Manifest     Alternative manifest (default tools\deps.json).
.PARAMETER ToolsDir     Alternative install root (default <repo>\.tools).
.PARAMETER Force        Re-extract even when the entry looks installed.
#>
[CmdletBinding()]
param(
    [string[]]$Only,
    [switch]$IncludeOptional,
    [switch]$Android,
    [switch]$Release,
    [switch]$Emulator,
    [switch]$All,
    [switch]$Offline,
    [string]$SeedFrom,
    [string]$Manifest,
    [string]$ToolsDir,
    [switch]$Force
)

Set-StrictMode -Version 2
$ErrorActionPreference = 'Stop'
$ProgressPreference = 'SilentlyContinue'

$root = Split-Path -Parent $PSScriptRoot
if (-not $Manifest) { $Manifest = Join-Path $PSScriptRoot 'deps.json' }
if (-not $ToolsDir) { $ToolsDir = Join-Path $root '.tools' }
$dl = Join-Path $ToolsDir 'dl'
$tar = Join-Path $env:SystemRoot 'System32\tar.exe'
if (-not (Test-Path -LiteralPath $tar)) { $tar = 'tar.exe' }

function Write-Step([string]$msg) { Write-Host $msg }

function Get-Sha256([string]$path) {
    (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
}

# Moves a file that failed verification aside; it is never used again and never overwritten silently.
function Move-Bad([string]$path) {
    $bad = "$path.bad"
    $n = 1
    while (Test-Path -LiteralPath $bad) { $n++; $bad = "$path.bad$n" }
    Move-Item -LiteralPath $path -Destination $bad
    return $bad
}

function Test-ArchiveHash([string]$path, $entry) {
    if (-not (Test-Path -LiteralPath $path)) { return $false }
    if ((Get-Item -LiteralPath $path).Length -ne [int64]$entry.size) { return $false }
    return (Get-Sha256 $path) -eq $entry.sha256.ToLowerInvariant()
}

function Resolve-Archive($entry) {
    $target = Join-Path $dl $entry.fileName
    if (Test-Path -LiteralPath $target) {
        if (Test-ArchiveHash $target $entry) { Write-Step "  archive present, hash ok: $($entry.fileName)"; return $target }
        $bad = Move-Bad $target
        Write-Warning "  existing $($entry.fileName) has the wrong hash; kept aside as $(Split-Path -Leaf $bad)"
    }
    New-Item -ItemType Directory -Force -Path $dl | Out-Null

    if ($SeedFrom) {
        if (-not (Test-Path -LiteralPath $SeedFrom -PathType Container)) { throw "-SeedFrom directory not found: $SeedFrom" }
        foreach ($f in Get-ChildItem -LiteralPath $SeedFrom -File) {
            if ($f.Length -ne [int64]$entry.size) { continue }
            if ((Get-Sha256 $f.FullName) -eq $entry.sha256.ToLowerInvariant()) {
                Copy-Item -LiteralPath $f.FullName -Destination $target
                Write-Step "  seeded from $($f.Name), hash ok"
                return $target
            }
        }
        Write-Step "  no matching archive in the seed directory"
    }

    if ($Offline) { throw "archive $($entry.fileName) is missing and -Offline was given (expected in $dl)" }

    $part = "$target.part"
    if (Test-Path -LiteralPath $part) { Remove-Item -LiteralPath $part -Force }   # our own unfinished download
    Write-Step "  downloading $($entry.url)"
    [Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12
    $wc = New-Object System.Net.WebClient
    $wc.Headers.Add('User-Agent', 'SteamPoweredPlayer-bootstrap')
    try { $wc.DownloadFile($entry.url, $part) } finally { $wc.Dispose() }
    if (-not (Test-ArchiveHash $part $entry)) {
        $bad = Move-Bad $part
        throw "SHA-256 or size mismatch for $($entry.fileName): downloaded file kept aside as $bad and NOT used (expected $($entry.sha256))"
    }
    Move-Item -LiteralPath $part -Destination $target
    Write-Step "  downloaded, hash ok"
    return $target
}

function Test-Installed($entry, [string]$dest) {
    $marker = Join-Path $dest '.installed.json'
    if (-not (Test-Path -LiteralPath $marker)) { return $false }
    try { $m = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json } catch { return $false }
    if ($m.sha256 -ne $entry.sha256) { return $false }
    foreach ($x in $entry.extract) {
        $p = Join-Path $dest $x.to
        if (-not (Test-Path -LiteralPath $p)) { return $false }
        if ($x.PSObject.Properties['sha256'] -and (Test-Path -LiteralPath $p -PathType Leaf)) {
            if ((Get-Sha256 $p) -ne $x.sha256.ToLowerInvariant()) { return $false }
        }
    }
    return $true
}

function Install-Entry($entry) {
    $dest = if ($entry.PSObject.Properties['sdkPath']) { Join-Path (Join-Path $ToolsDir 'android\sdk') $entry.sdkPath }
            else { Join-Path (Join-Path $ToolsDir $entry.id) $entry.version }
    Write-Step "[$($entry.id) $($entry.version)]"

    if (-not $Force -and (Test-Installed $entry $dest)) { Write-Step "  already installed: $dest"; return }

    $archive = Resolve-Archive $entry

    $tmp = Join-Path $dl (".x-$($entry.id)-" + [guid]::NewGuid().ToString('N').Substring(0, 8))
    New-Item -ItemType Directory -Force -Path $tmp | Out-Null
    try {
        Write-Step "  extracting ($($entry.archive))"
        if ($entry.archive -eq 'inno-installer') {
            # An Inno Setup installer: run it silently, for the current user only, into the scratch folder (nothing system-wide, no
            # tasks such as file associations). Its HKCU uninstall entry is harmless; the files are copied out below.
            $p = Start-Process -FilePath $archive -Wait -PassThru -ArgumentList @('/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', '/CURRENTUSER', '/TASKS=', "/DIR=$tmp")
            if ($p.ExitCode -ne 0) { throw "the installer $($entry.fileName) exited with code $($p.ExitCode)" }
        } else {
            & $tar -xf $archive -C $tmp
            if ($LASTEXITCODE -ne 0) { throw "tar failed with exit code $LASTEXITCODE for $($entry.fileName)" }
        }

        $src = $tmp
        if ($entry.stripRoot) {
            $children = @(Get-ChildItem -LiteralPath $tmp -Force)
            if ($children.Count -ne 1 -or -not $children[0].PSIsContainer) {
                throw "stripRoot is set but the archive does not have a single top-level folder"
            }
            $src = $children[0].FullName
        }

        New-Item -ItemType Directory -Force -Path $dest | Out-Null
        $items = @($entry.extract)
        if ($items.Count -eq 0) { $items = @([pscustomobject]@{ from = '.'; to = '.' }) }
        foreach ($x in $items) {
            $from = if ($x.from -eq '.') { $src } else { Join-Path $src $x.from }
            $to = if ($x.to -eq '.') { $dest } else { Join-Path $dest $x.to }
            if (-not (Test-Path -LiteralPath $from)) { throw "expected '$($x.from)' not found in the archive" }
            if (Test-Path -LiteralPath $from -PathType Container) {
                New-Item -ItemType Directory -Force -Path $to | Out-Null
                Copy-Item -Path (Join-Path $from '*') -Destination $to -Recurse -Force
            } else {
                $parent = Split-Path -Parent $to
                if ($parent) { New-Item -ItemType Directory -Force -Path $parent | Out-Null }
                Copy-Item -LiteralPath $from -Destination $to -Force
                if ($x.PSObject.Properties['sha256']) {
                    $h = Get-Sha256 $to
                    if ($h -ne $x.sha256.ToLowerInvariant()) {
                        $bad = Move-Bad $to
                        throw "SHA-256 mismatch for extracted $($x.to): got $h, expected $($x.sha256); kept aside as $bad"
                    }
                    Write-Step "  verified $($x.to)"
                }
            }
        }
        $marker = [ordered]@{ id = $entry.id; version = $entry.version; sha256 = $entry.sha256 } | ConvertTo-Json
        Set-Content -LiteralPath (Join-Path $dest '.installed.json') -Value $marker -Encoding UTF8
        Write-Step "  installed: $dest"
    }
    finally {
        if (Test-Path -LiteralPath $tmp) { Remove-Item -LiteralPath $tmp -Recurse -Force }   # our own scratch folder
    }
}

if (-not (Test-Path -LiteralPath $Manifest)) { throw "manifest not found: $Manifest" }
$deps = Get-Content -LiteralPath $Manifest -Raw | ConvertFrom-Json
$entries = @($deps.entries)

if ($Only) {
    foreach ($id in $Only) { if (-not ($entries | Where-Object { $_.id -eq $id })) { throw "unknown id '$id' (known: $(($entries | ForEach-Object { $_.id }) -join ', '))" } }
    $selected = @($entries | Where-Object { $Only -contains $_.id })
} else {
    $selected = @($entries | Where-Object {
        $isOpt = [bool]$_.optional
        ($All) -or
        ($_.group -eq 'desktop' -and (-not $isOpt -or $IncludeOptional)) -or
        ($Release -and ($_.group -eq 'release' -or $_.id -eq 'ffmpeg')) -or
        ($Android -and $_.group -eq 'android') -or
        ($Emulator -and $_.group -eq 'emulator')
    })
}

$failed = @()
foreach ($e in $selected) {
    try { Install-Entry $e }
    catch { $failed += $e.id; Write-Host "  ERROR [$($e.id)]: $($_.Exception.Message)" -ForegroundColor Red }
}

if ($failed.Count -gt 0) {
    Write-Host "bootstrap failed for: $($failed -join ', ')" -ForegroundColor Red
    exit 1
}
Write-Host "bootstrap done ($($selected.Count) entr$(if ($selected.Count -eq 1) { 'y' } else { 'ies' }))."
exit 0
