# SPDX-License-Identifier: GPL-3.0-or-later
<#
Spike S1, PART B: every timed measurement, in one go (<= ~25 min). Run ONLY on an otherwise idle machine (close games/browsers/Steam
overlays; no other heavy process). The script refuses to start when the machine is busy unless -Force is given (then the summary says so).

  1. APP  : S1App --mode play  (libmpv D3D11 render API in a SwapChainPanel, hwdec=d3d11va, lead EDL, 1x, looping by reload)
            -> present stats / frame timing, process CPU per media-second (% of one core and of all logical cores), RAM + GPU memory,
               seek-by-reload latency
  2. REF  : mpv CLI (same build family, hwdec=d3d11va, same EDL, same start, 1280x720 window) sampled externally the same way
  3. Order is interleaved: app, ref, app, ref, ... (-Repeats times), then ONE app soak of -SoakSeconds (default 600 = 10 media-minutes)
  4. Summary: median + spread per metric, hardware CLASS only (GPU vendor + logical cores), written to <Out>/summary.md and summary.json

Nothing is killed or deleted: the app exits by itself, the reference mpv is stopped with its IPC "quit" command.
-Smoke : ~15 s plays, 1 repeat, 20 s soak (<= 60 s total). Output goes to out/perf-smoke and is labelled SMOKE (NOT results).
Usage (repo root, PowerShell):  spikes\001-winui-mpv-d3d11\perf\run-perf.ps1 [-Smoke] [-Repeats 3] [-PlaySeconds 60] [-SoakSeconds 600] [-Force]
Prereq: spikes\001-winui-mpv-d3d11\out\bin\S1App.exe built (see README) and the fixture folder (read-only) given by -Fixture.
#>
[CmdletBinding()]
param(
    [switch]$Smoke,
    [int]$Repeats = 3,
    [int]$PlaySeconds = 60,
    [int]$WarmupSeconds = 8,
    [int]$Seeks = 10,
    [int]$SoakSeconds = 600,
    [switch]$Force,
    [string]$Fixture = '',
    [string]$ToolsDir = '',
    [string]$Out = ''
)
$ErrorActionPreference = 'Stop'
$inv = [System.Globalization.CultureInfo]::InvariantCulture
$spike = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path.Replace('\', '/')
if ($ToolsDir -eq '') { $ToolsDir = (Resolve-Path (Join-Path $PSScriptRoot '../../../../../.tools')).Path.Replace('\', '/') }
if ($Fixture -eq '') { $Fixture = "$ToolsDir/fixtures-local/s2/stale-100" }
$label = 'RESULTS'
if ($Smoke) { $Repeats = 1; $PlaySeconds = 15; $WarmupSeconds = 3; $Seeks = 2; $SoakSeconds = 20; $label = 'SMOKE (not results)' }
if ($Out -eq '') { $Out = if ($Smoke) { "$spike/out/perf-smoke" } else { "$spike/out/perf" } }
New-Item -ItemType Directory -Force -Path $Out | Out-Null
$app = "$spike/out/bin/S1App.exe"
$refMpv = (Get-ChildItem -Path "$ToolsDir/mpv" -Recurse -Filter mpv.exe | Select-Object -First 1).FullName.Replace('\', '/')
if (-not (Test-Path $app)) { throw "build the app first: $app missing" }
$cores = [Environment]::ProcessorCount
$gpuVendor = 'unknown'
$vc = (Get-CimInstance Win32_VideoController | Select-Object -ExpandProperty Name) -join ';'
if ($vc -match 'NVIDIA') { $gpuVendor = 'NVIDIA' } elseif ($vc -match 'AMD|Radeon') { $gpuVendor = 'AMD' } elseif ($vc -match 'Intel') { $gpuVendor = 'Intel' }
$hwClass = "desktop $gpuVendor GPU, $cores logical cores"

# ---- idle gate -------------------------------------------------------------------------------------------------------
$pc = New-Object System.Diagnostics.PerformanceCounter('Processor', '% Processor Time', '_Total')
$null = $pc.NextValue(); Start-Sleep -Seconds 3; $idleCpu = [math]::Round($pc.NextValue(), 1)
Write-Host "machine CPU before start: $idleCpu %"
$idle = $idleCpu -lt 10
if (-not $idle -and -not $Force) { throw "machine busy ($idleCpu % CPU). Close other programs or pass -Force (summary will say 'NOT IDLE')." }

# ---- helpers ---------------------------------------------------------------------------------------------------------
function Get-Median([double[]]$x) {
    $s = @($x | Where-Object { -not [double]::IsNaN($_) } | Sort-Object)
    if ($s.Count -eq 0) { return [double]::NaN }
    return $s[[int][math]::Floor($s.Count / 2)]
}
function Fmt([double]$d) { return $d.ToString('F3', $inv) }
function Invoke-MpvIpc([string]$pipe, [string]$name, $args2) {
    $c = New-Object System.IO.Pipes.NamedPipeClientStream('.', $pipe, [System.IO.Pipes.PipeDirection]::InOut)
    try {
        $c.Connect(3000)
        $w = New-Object System.IO.StreamWriter($c); $w.AutoFlush = $true
        $r = New-Object System.IO.StreamReader($c)
        $w.WriteLine((@{ command = @($name) + $args2; request_id = 77 } | ConvertTo-Json -Compress))
        if ($name -eq 'quit') { return $null }
        for ($i = 0; $i -lt 30; $i++) {
            $t = $r.ReadLineAsync()
            if (-not $t.Wait(2000)) { return $null }
            $o = $t.Result | ConvertFrom-Json
            if ($o.request_id -eq 77) { return $o.data }
        }
    } catch { return $null } finally { $c.Dispose() }
    return $null
}

$runs = @()   # per-run result objects
function Run-App([string]$tag, [int]$seconds, [int]$seeks) {
    $argList = @('--mode', 'play', '--fixture', $Fixture, '--out', $Out, '--tag', $tag, '--seconds', "$seconds", '--warmup', "$WarmupSeconds", '--seeks', "$seeks")
    $p = Start-Process -FilePath $app -ArgumentList $argList -PassThru -Wait
    $j = Get-Content -Raw "$Out/$tag.json" | ConvertFrom-Json
    return [pscustomobject]@{ Kind = 'app'; Tag = $tag; ExitCode = $p.ExitCode; Json = $j
        Cpu1 = $j.cpuPctOfOneCorePerMediaSecond; CpuAll = $j.cpuPctOfAllCores; WsMax = $j.wsMbMax; GpuMax = $j.gpuLocalMbMax
        SeekMed = $j.seekLatencyMedianMs; PresentsPerSec = $j.presentsPerSec }
}
function Run-Ref([string]$tag, [string]$edl, [double]$t0, [int]$seconds) {
    $pipe = "s1ref-$tag"
    $log = "$Out/$tag.mpv.log"
    $margs = @('--no-config', '--hwdec=d3d11va', '--vo=gpu-next', '--gpu-context=d3d11', '--geometry=1280x720', '--no-osc', '--no-osd-bar',
        '--keep-open=yes', '--force-window=yes', '--audio=no', "--start=$($t0.ToString('F3', $inv))", "--input-ipc-server=\\.\pipe\$pipe", "--log-file=$log", '--msg-level=all=warn', $edl)
    $p = Start-Process -FilePath $refMpv -ArgumentList $margs -PassThru
    $csv = "t_s,cpu_s,ws_mb,private_mb,time_pos`n"
    $sw = [System.Diagnostics.Stopwatch]::StartNew()
    $cpu0 = 0.0; $tp0 = [double]::NaN; $wall0 = 0.0; $reset = $false; $wsMax = 0.0; $next = 0.0
    while ($sw.Elapsed.TotalSeconds -lt ($WarmupSeconds + $seconds)) {
        $now = $sw.Elapsed.TotalSeconds
        if (-not $reset -and $now -ge $WarmupSeconds) {
            $reset = $true; $p.Refresh(); $cpu0 = $p.TotalProcessorTime.TotalSeconds; $wall0 = $now
            $v = Invoke-MpvIpc $pipe 'get_property' @('time-pos'); if ($null -ne $v) { $tp0 = [double]$v }
        }
        if ($now -ge $next) {
            $next = $now + 2; $p.Refresh(); $tpv = Invoke-MpvIpc $pipe 'get_property' @('time-pos')
            $ws = $p.WorkingSet64 / 1MB
            if ($reset -and $ws -gt $wsMax) { $wsMax = $ws }
            $csv += ('{0:F1},{1:F3},{2:F1},{3:F1},{4}' -f $now, $p.TotalProcessorTime.TotalSeconds, $ws, ($p.PrivateMemorySize64 / 1MB), $tpv).Replace(',', ',') + "`n"
        }
        Start-Sleep -Milliseconds 200
    }
    $p.Refresh(); $cpuUsed = $p.TotalProcessorTime.TotalSeconds - $cpu0; $wall = $sw.Elapsed.TotalSeconds - $wall0
    $v = Invoke-MpvIpc $pipe 'get_property' @('time-pos'); $tp1 = if ($null -ne $v) { [double]$v } else { [double]::NaN }
    $drops = Invoke-MpvIpc $pipe 'get_property' @('frame-drop-count')
    $null = Invoke-MpvIpc $pipe 'quit' @()
    $null = $p.WaitForExit(8000)
    Set-Content -Path "$Out/$tag.csv" -Value $csv -Encoding utf8
    $media = $tp1 - $tp0
    if ([double]::IsNaN($media) -or $media -le 0) { $media = $wall }
    $c1 = 100.0 * $cpuUsed / $media
    $res = [pscustomobject]@{ tag = $tag; hwdec = 'd3d11va'; wallS = $wall; mediaS = $media; cpuSeconds = $cpuUsed
        cpuPctOfOneCorePerMediaSecond = $c1; cpuPctOfAllCores = $c1 / $cores; wsMbMax = $wsMax; frameDropCount = $drops }
    $res | ConvertTo-Json | Set-Content -Path "$Out/$tag.json" -Encoding utf8
    return [pscustomobject]@{ Kind = 'ref'; Tag = $tag; ExitCode = $p.ExitCode; Json = $res; Cpu1 = $c1; CpuAll = $c1 / $cores; WsMax = $wsMax }
}

# ---- run -------------------------------------------------------------------------------------------------------------
$sw0 = [System.Diagnostics.Stopwatch]::StartNew()
$appRuns = @(); $refRuns = @(); $edl = ''; $t0 = 0.0
for ($i = 1; $i -le $Repeats; $i++) {
    Write-Host "== repeat $i/$Repeats : app"
    $a = Run-App "app-r$i" $PlaySeconds $Seeks; $appRuns += $a
    if ($edl -eq '') {
        $edl = "$Out/app-r1.edl"
        $m = [regex]::Match((Get-Content -Raw $edl), 'chunk-stream0-\d+\.m4s,length=(\d+)')
        $t0 = [double]::Parse($m.Groups[1].Value, $inv) - 3.0     # lead EDL: first chunk length = first tfdt end; start = tfdt - one 3 s segment
    }
    Write-Host ("   app cpu {0}% of one core, {1}% of all cores" -f (Fmt $a.Cpu1), (Fmt $a.CpuAll))
    Write-Host "== repeat $i/$Repeats : reference mpv CLI"
    $r = Run-Ref "ref-r$i" $edl $t0 $PlaySeconds; $refRuns += $r
    Write-Host ("   ref cpu {0}% of one core, {1}% of all cores" -f (Fmt $r.Cpu1), (Fmt $r.CpuAll))
}
Write-Host "== soak: app, $SoakSeconds s (= $([math]::Round($SoakSeconds / 60.0, 1)) media-minutes)"
$soak = Run-App 'app-soak' $SoakSeconds 0

# ---- summary ---------------------------------------------------------------------------------------------------------
function Spread($runs, [string]$prop) {
    $v = @($runs | ForEach-Object { [double]$_.$prop })
    $s = @($v | Sort-Object)
    return [pscustomobject]@{ median = (Get-Median $v); min = $s[0]; max = $s[-1]; n = $s.Count }
}
$sum = [ordered]@{
    label = $label; hardwareClass = $hwClass; machineCpuBeforeStartPct = $idleCpu; machineIdle = $idle; forced = [bool]$Force
    repeats = $Repeats; playSeconds = $PlaySeconds; warmupSeconds = $WarmupSeconds; soakSeconds = $SoakSeconds; totalWallS = [math]::Round($sw0.Elapsed.TotalSeconds, 0)
    app = [ordered]@{ cpuPctOfOneCore = (Spread $appRuns 'Cpu1'); cpuPctOfAllCores = (Spread $appRuns 'CpuAll'); wsMbMax = (Spread $appRuns 'WsMax')
        gpuLocalMbMax = (Spread $appRuns 'GpuMax'); seekLatencyMedianMs = (Spread $appRuns 'SeekMed'); presentsPerSec = (Spread $appRuns 'PresentsPerSec') }
    ref = [ordered]@{ cpuPctOfOneCore = (Spread $refRuns 'Cpu1'); cpuPctOfAllCores = (Spread $refRuns 'CpuAll'); wsMbMax = (Spread $refRuns 'WsMax') }
    soak = [ordered]@{ mediaS = $soak.Json.mediaS; wsMbMax = $soak.WsMax; privateMbMax = $soak.Json.privateMbMax; gpuLocalMbFirst = $soak.Json.gpuLocalMbFirst
        gpuLocalMbLast = $soak.Json.gpuLocalMbLast; gpuLocalMbMax = $soak.Json.gpuLocalMbMax; cpuPctOfOneCore = $soak.Cpu1; present = $soak.Json.present }
    budgets = [ordered]@{ seekMs = 150; openToFirstFrameMs = 250; cpuPctOfAllCores1440p60 = 3; ramMb = 250 }
}
$sum | ConvertTo-Json -Depth 8 | Set-Content -Path "$Out/summary.json" -Encoding utf8
$md = @("# S1 perf summary: $label", '', "Hardware class: $hwClass. Machine CPU before start: $idleCpu % (idle=$idle, forced=$([bool]$Force)).",
    "Repeats: $Repeats interleaved (app, ref), $PlaySeconds s each after $WarmupSeconds s warm-up; soak $SoakSeconds s. Total wall: $($sum.totalWallS) s.", '',
    '| metric | median | min | max | budget |', '|---|---|---|---|---|')
foreach ($row in @(
    @('app CPU % of one core', $sum.app.cpuPctOfOneCore, ''), @('app CPU % of all cores', $sum.app.cpuPctOfAllCores, '< 3'),
    @('ref (mpv CLI) CPU % of one core', $sum.ref.cpuPctOfOneCore, ''), @('ref (mpv CLI) CPU % of all cores', $sum.ref.cpuPctOfAllCores, ''),
    @('app working set max MB', $sum.app.wsMbMax, '< 250'), @('app GPU local MB max', $sum.app.gpuLocalMbMax, ''),
    @('app seek-by-reload latency ms (median per run)', $sum.app.seekLatencyMedianMs, '< 150'), @('app presents/s', $sum.app.presentsPerSec, '~60'))) {
    $s = $row[1]; $md += ('| {0} | {1} | {2} | {3} | {4} |' -f $row[0], (Fmt $s.median), (Fmt $s.min), (Fmt $s.max), $row[2])
}
$md += ''; $md += "Soak ($([math]::Round($soak.Json.mediaS / 60.0, 1)) media-min): working set max $(Fmt $soak.WsMax) MB, private max $(Fmt $soak.Json.privateMbMax) MB, GPU local first/last/max $(Fmt $soak.Json.gpuLocalMbFirst) / $(Fmt $soak.Json.gpuLocalMbLast) / $(Fmt $soak.Json.gpuLocalMbMax) MB."
$md += 'Present stats / frame timing per run: see present{} in <tag>.json. Raw per-2 s samples: <tag>.csv.'
$md | Set-Content -Path "$Out/summary.md" -Encoding utf8
Write-Host "done in $($sw0.Elapsed.TotalSeconds.ToString('F0', $inv)) s -> $Out/summary.md  [$label]"
