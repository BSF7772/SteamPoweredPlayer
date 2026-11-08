# SPDX-License-Identifier: GPL-3.0-or-later
# One command for the whole automated check, on the real library: build, unit tests + --selftest (at the same time), then --uicheck,
# which needs the machine to itself. Fails when any part fails or the run takes over 5 minutes. Writes reports to TMP\e2e (git-ignored).
# Close the app first: a running copy locks the build output. Keep hands off the window while --uicheck drives it.
#   powershell -ExecutionPolicy Bypass -File tools\e2e.ps1
$ErrorActionPreference = 'Stop'
$budgetSeconds = 300
$root = Split-Path -Parent $PSScriptRoot
Set-Location $root
if (Get-Process SteamPoweredPlayer -ErrorAction SilentlyContinue) { Write-Host 'Close SteamPoweredPlayer first: it locks the build output.'; exit 2 }

$out = Join-Path $root 'TMP\e2e'
New-Item -ItemType Directory -Force $out | Out-Null
$exe = Join-Path $root 'src\bin\x64\Release\net10.0-windows10.0.22621.0\win-x64\SteamPoweredPlayer.exe'
$total = [Diagnostics.Stopwatch]::StartNew()
$results = @()

function Add-Result($name, $ok, $seconds, $detail) {
    $script:results += [pscustomobject]@{ Part = $name; Result = $(if ($ok) { 'PASS' } else { 'FAIL' }); Seconds = [int]$seconds; Detail = $detail }
}

function Summary($report) {
    if (Test-Path $report) { (Select-String -Path $report -Pattern '^Summary:' | Select-Object -Last 1).Line } else { 'no report' }
}

$sw = [Diagnostics.Stopwatch]::StartNew()
dotnet build src -c Release -nologo -v q | Out-File (Join-Path $out 'build.txt')
Add-Result 'Build' ($LASTEXITCODE -eq 0) $sw.Elapsed.TotalSeconds ''
if ($LASTEXITCODE -ne 0) { $results | Format-Table -AutoSize; Get-Content (Join-Path $out 'build.txt') -Tail 15; exit 1 }

$sw.Restart()
$unitLog = Join-Path $out 'unit.txt'
$unit = Start-Process dotnet -ArgumentList 'test', 'tests', '--nologo', '-v', 'q' -NoNewWindow -PassThru -RedirectStandardOutput $unitLog -RedirectStandardError (Join-Path $out 'unit-err.txt')
$selfReport = Join-Path $out 'selftest.txt'
$self = Start-Process $exe -ArgumentList '--selftest', "`"$selfReport`"" -PassThru
$null = $unit.Handle; $null = $self.Handle        # without a cached handle, ExitCode stays empty after the exit
$unit.WaitForExit(); $self.WaitForExit()
$unitLine = (Select-String -Path $unitLog -Pattern 'Passed!|Failed!' | Select-Object -Last 1).Line
Add-Result 'Unit tests' ($unit.ExitCode -eq 0) $sw.Elapsed.TotalSeconds ($unitLine -replace '^.*?(Failed:)', '$1' -replace ', Duration.*$', '')
Add-Result '--selftest' ($self.ExitCode -eq 0) $sw.Elapsed.TotalSeconds (Summary $selfReport)

$sw.Restart()
$uiReport = Join-Path $out 'uicheck.txt'
$ui = Start-Process $exe -ArgumentList '--uicheck', "`"$uiReport`"", '--captures', "`"$(Join-Path $out 'captures')`"" -PassThru
$null = $ui.Handle
$ui.WaitForExit()
Add-Result '--uicheck' ($ui.ExitCode -eq 0) $sw.Elapsed.TotalSeconds (Summary $uiReport)

$withinBudget = $total.Elapsed.TotalSeconds -le $budgetSeconds
Add-Result "Total (budget $budgetSeconds s)" $withinBudget $total.Elapsed.TotalSeconds ''
$results | Format-Table -AutoSize
foreach ($report in $selfReport, $uiReport) { if (Test-Path $report) { Select-String -Path $report -Pattern '^FAIL' | ForEach-Object { $_.Line } } }
if ($results.Result -contains 'FAIL') { exit 1 }
Write-Host 'E2E PASS'
