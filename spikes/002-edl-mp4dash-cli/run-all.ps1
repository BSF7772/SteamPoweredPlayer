# SPDX-License-Identifier: GPL-3.0-or-later
# Spike S2: run every measurement. READ-ONLY on the recording library; the share probe runs on a COPY.
# Build first:  dotnet build spikes\002-edl-mp4dash-cli\S2Tool -c Release -o spikes\002-edl-mp4dash-cli\out\bin
# Usage (PowerShell, from the repo root):
#   spikes\002-edl-mp4dash-cli\run-all.ps1 -Mpv <.tools\mpv\mpv-YYYYMMDD\mpv.exe> -Stale <RecordingRoot>\video\bg_<stale 2 h session> `
#       -Crashed <RecordingRoot>\video\bg_<crashed session> -Copy <fixtures-local>\s2\stale-100
# Every scenario runs twice: "classic" = EDL exactly as the plan's task 1.6 sketch (each part length=3),
#                            "lead"    = EDL whose first part is N0*3 s long and whose parts before gaps are stretched (EDL axis == tfdt axis).
param(
  [Parameter(Mandatory)][string]$Mpv,
  [Parameter(Mandatory)][string]$Stale,
  [Parameter(Mandatory)][string]$Crashed,
  [Parameter(Mandatory)][string]$Copy,
  [string]$Out = "",
  [string]$Bin = ""
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Out) { $Out = "$here/out" }
if (-not $Bin) { $Bin = "$here/out/bin" }
$tool = "$Bin/S2Tool.dll"
function Run([string[]]$a) { & dotnet $tool @a | Out-Null; if ($LASTEXITCODE -ne 0) { Write-Warning "scenario failed: $a" } }

& dotnet $tool probe-selftest "$Out/probe-control" | Set-Content "$Out/probe-selftest.json" -Encoding utf8
foreach ($s in @($Stale, $Crashed)) {
  $n = Split-Path $s -Leaf
  & dotnet $tool info $s      | Set-Content "$Out/info-$n.json" -Encoding utf8
  & dotnet $tool integrity $s | Set-Content "$Out/integrity-$n.json" -Encoding utf8
}
foreach ($mode in 'classic','lead') {
  $lead = if ($mode -eq 'lead') { @('--lead') } else { @() }
  $o = "$Out/$mode"
  Run (@('run','explore',  $Stale,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va') + $lead)
  Run (@('run','coldopen', $Stale,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va') + $lead)
  Run (@('run','prewarm',  $Stale,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va') + $lead)
  Run (@('run','basic',    $Stale,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va') + $lead)
  Run (@('run','basic',    $Stale,'--mpv',$Mpv,'--hwdec','no',     '--out',$o,'--tag','cpu') + $lead)
  Run (@('run','basic',    $Stale,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va-lowmem','--extra','--demuxer-max-bytes=24MiB;--demuxer-max-back-bytes=8MiB') + $lead)
  Run (@('run','gap',      $Stale,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va') + $lead)
  Run (@('run','zero',     $Stale,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va') + $lead)
  Run (@('run','eof',      $Stale,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va') + $lead)
  Run (@('run','eof',      $Stale,'--mpv',$Mpv,'--hwdec','no',     '--out',$o,'--tag','cpu') + $lead)
  Run (@('run','crashed',  $Crashed,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va') + $lead)
  Run (@('run','share',    $Copy,'--mpv',$Mpv,'--hwdec','d3d11va','--out',$o,'--tag','d3d11va') + $lead)
}
