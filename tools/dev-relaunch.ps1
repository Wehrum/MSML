<#
.SYNOPSIS
    Development loop: builds everything (stopping there if it fails, with the game still running), closes the game,
    deploys, relaunches through Steam, and waits for the main menu. Needs the DevHarness plugin (built as part of the
    solution).
#>
[CmdletBinding()]
param(
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers",
    [switch]$NoBuild,
    [int]$LoadSlot = 0,
    [int]$TimeoutSec = 90
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$log = Join-Path $GameDir "BepInEx\LogOutput.log"

$solution = Join-Path $RepoRoot "MoonSweepMods.sln"
if (-not $NoBuild) {
    # Compile first without deploying (the running game locks its DLLs): a broken build leaves the game running.
    & dotnet build $solution -c Release -nologo -v q "-p:GameDir=$GameDir" -p:DeployToGame=false | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed (the game was left running)." }
}

$proc = Get-Process MoonSweepers -ErrorAction SilentlyContinue
if ($proc) {
    Write-Host "Closing the game..."
    [IO.File]::WriteAllLines((Join-Path $GameDir "BepInEx\devharness.in"), @("quit"))
    if (-not $proc.WaitForExit(15000)) { $proc.CloseMainWindow() | Out-Null; if (-not $proc.WaitForExit(10000)) { throw "The game didn't close." } }
    Start-Sleep -Seconds 2   # let Steam notice the game exited
}
# A "quit" the harness never read would close the next launch as soon as it starts.
Remove-Item (Join-Path $GameDir "BepInEx\devharness.in"), (Join-Path $GameDir "BepInEx\devharness.out") -ErrorAction SilentlyContinue

if (-not $NoBuild) {
    & dotnet build $solution -c Release -nologo -v q "-p:GameDir=$GameDir" | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Deploying failed." }
}

$started = Get-Date
Start-Process "steam://rungameid/5335510"
$deadline = $started.AddSeconds($TimeoutSec)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
    if ((Test-Path $log) -and (Get-Item $log).LastWriteTime -gt $started -and (Select-String -Path $log -Pattern "DevHarness: main menu ready" -Quiet)) {
        Start-Sleep -Seconds 2
        Write-Host "Main menu ready." -ForegroundColor Green
        if ($LoadSlot -gt 0) {
            $dev = Join-Path $PSScriptRoot "dev.ps1"
            Start-Sleep -Seconds 4   # past the studio splash
            & $dev "click menu.home.START GAME" | Out-Null
            & $dev "click menu.frame.Start screen.Save slot $LoadSlot" | Out-Null
            & $dev "click menu.frame.Start screen.Play" | Out-Null
            $inGameBy = (Get-Date).AddSeconds(60)
            while ((Get-Date) -lt $inGameBy) {
                Start-Sleep -Seconds 2
                $probe = & $dev "get find:MoonPrototypeEquipment.Player" 2>$null
                if ($probe -match '^MoonWalker') { Start-Sleep -Seconds 6; Write-Host "In game (slot $LoadSlot)." -ForegroundColor Green; exit 0 }
            }
            throw "Didn't get into the game from slot $LoadSlot."
        }
        exit 0
    }
}
throw "Main menu didn't appear within $TimeoutSec s. See $log"
