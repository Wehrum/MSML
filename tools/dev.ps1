<#
.SYNOPSIS
    Sends commands to the DevHarness plugin in the running game and prints the results.

.EXAMPLE
    .\tools\dev.ps1 "dump menu.home 2" "shot C:\temp\menu.png"
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Mandatory = $true, Position = 0, ValueFromRemainingArguments = $true)][string[]]$Commands,
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers",
    [int]$TimeoutSec = 20
)

$ErrorActionPreference = "Stop"
$bepinex = Join-Path $GameDir "BepInEx"
$in = Join-Path $bepinex "devharness.in"
$out = Join-Path $bepinex "devharness.out"
if (-not (Get-Process MoonSweepers -ErrorAction SilentlyContinue)) { throw "Moon Sweepers isn't running." }

Remove-Item $out -ErrorAction SilentlyContinue
[IO.File]::WriteAllLines($in, $Commands)
$deadline = (Get-Date).AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Milliseconds 250
    if ((Test-Path $out) -and ((Get-Content $out -Raw) -match '<<done>>')) {
        Get-Content $out | Where-Object { $_ -ne "<<done>>" }
        exit 0
    }
    if (-not (Get-Process MoonSweepers -ErrorAction SilentlyContinue)) {
        if (Test-Path $out) { Get-Content $out }
        throw "Moon Sweepers exited (crashed?) while running the command. See BepInEx\LogOutput.log and the Windows Application event log."
    }
}
throw "No response from the dev harness within $TimeoutSec s (is MoonSweepMods.DevHarness deployed?)."
