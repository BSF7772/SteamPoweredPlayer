# SPDX-License-Identifier: GPL-3.0-or-later
# Spike S2: follow-up runs after run-timed.ps1 (idle machine): windowed-EDL reload seeks (x3, variants interleaved) and memory growth (x1 per variant).
param(
  [Parameter(Mandatory)][string]$Mpv,
  [Parameter(Mandatory)][string]$Stale,
  [int]$Reps = 3,
  [string]$Out = "",
  [string]$Bin = ""
)
$ErrorActionPreference = 'Stop'
$here = Split-Path -Parent $MyInvocation.MyCommand.Path
if (-not $Out) { $Out = "$here/out/final" }
if (-not $Bin) { $Bin = "$here/out/bin" }
$tool = "$Bin/S2Tool.dll"
function Run([string]$scn, [string]$hw, [int]$rep) {
  $variant = if ($hw -eq 'no') { 'cpu' } else { $hw }
  & dotnet $tool run $scn $Stale --mpv $Mpv --hwdec $hw --out "$Out/lead" --tag "$variant-r$rep" --lead | Out-Null
  if ($LASTEXITCODE -ne 0) { Write-Warning "failed: $scn $variant r$rep" }
}
for ($rep = 1; $rep -le $Reps; $rep++) { foreach ($hw in 'd3d11va','no') { Run 'seekwin' $hw $rep } }
foreach ($hw in 'd3d11va','no') { Run 'memgrow' $hw 1 }
