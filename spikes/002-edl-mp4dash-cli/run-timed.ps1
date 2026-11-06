# SPDX-License-Identifier: GPL-3.0-or-later
# Spike S2: every TIMED scenario, N repeats, variants (d3d11va / cpu) interleaved inside each repeat. Machine must be otherwise idle.
# Build first:  dotnet build spikes\002-edl-mp4dash-cli\S2Tool -c Release -o spikes\002-edl-mp4dash-cli\out\bin
# Then pool the repeats:  dotnet spikes\002-edl-mp4dash-cli\out\bin\S2Tool.dll summary <Out>\lead <Out>\classic
param(
  [Parameter(Mandatory)][string]$Mpv,
  [Parameter(Mandatory)][string]$Stale,
  [Parameter(Mandatory)][string]$Crashed,
  [int]$Reps = 3,
  [string]$Out = "",
  [string]$Bin = ""
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Out) { $Out = "$here/out/final" }
if (-not $Bin) { $Bin = "$here/out/bin" }
$tool = "$Bin/S2Tool.dll"
function Run([string]$mode, [string]$scn, [string]$session, [string]$hw, [int]$rep) {
  $variant = if ($hw -eq 'no') { 'cpu' } else { $hw }
  $a = @('run', $scn, $session, '--mpv', $Mpv, '--hwdec', $hw, '--out', "$Out/$mode", '--tag', "$variant-r$rep")
  if ($mode -eq 'lead') { $a += '--lead' }
  & dotnet $tool @a | Out-Null
  if ($LASTEXITCODE -ne 0) { Write-Warning "failed: $mode $scn $variant r$rep" }
}
for ($rep = 1; $rep -le $Reps; $rep++) {
  foreach ($scn in 'coldopen','prewarm','basic','seekmodes','eof','speed') {
    foreach ($hw in 'd3d11va','no') { Run 'lead' $scn $Stale $hw $rep }
  }
  foreach ($hw in 'd3d11va','no') { Run 'lead' 'crashed' $Crashed $hw $rep }
  # classic EDL (the plan's task 1.6 sketch) as the baseline, hardware decode only
  foreach ($scn in 'coldopen','basic','eof') { Run 'classic' $scn $Stale 'd3d11va' $rep }
}
