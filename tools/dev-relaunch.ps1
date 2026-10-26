<#
.SYNOPSIS
    Development loop: closes the game, builds and deploys everything, relaunches through Steam, and waits for the
    main menu. Needs the DevHarness plugin (built as part of the solution).
#>
[CmdletBinding()]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers",
    [switch]$NoBuild,
    [int]$TimeoutSec = 90
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$log = Join-Path $GameDir "BepInEx\LogOutput.log"

$proc = Get-Process MoonSweepers -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Closing the game..."
    [IO.File]::WriteAllLines((Join-Path $GameDir "BepInEx\devharness.in"), @("quit"))
    if (-not $proc.WaitForExit(15000)) { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(10000)) { throw "The game didn't close." } }
    Start-Sleep -Seconds 2   # let Steam notice the game exited
}

if (-not $NoBuild) {
    & dotnet build (Join-Path $RepoRoot "MoonSweepMods.sln") -c Release -nologo -v q "-p:GameDir=$GameDir" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
}

$started = Get-Date
Start-Process "steam://rungameid/5335510"
$deadline = $started.AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
    if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $started -and (Select-String -Path $log -Pattern "DevHarness: main menu ready" -Quiet)) {
        Start-Sleep -Seconds 2
        Write-Host "Main menu ready." -ForegroundColor Green
        exit 0
    }
}
throw "Main menu didn't appear within $TimeoutSec s. See $log"
