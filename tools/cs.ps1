<#
.SYNOPSIS
    Runs a C# script (or a snippet) inside the running game through the DevHarness. No restart: it's compiled with
    Roslyn in the game, so a change takes seconds to try.

.DESCRIPTION
    A script is a .cs file with a static Run method on any class:

        public static class Script
        {
            public static IEnumerator Run(ScriptContext ctx)   // or: object Run(ScriptContext ctx), or void
            {
                var v = Spawn("Push sweeper");
                yield return 1f;                                // wait 1 s (null = one frame)
                yield return Board(v);                          // run another iterator
                ctx.Log($"driving {Driving?.Spec?.name}");
            }
        }

    Everything in the game (MoonCleaners, UnityEngine, the interop assemblies, loaded mods) can be used, plus the
    helpers in tools\DevHarness\Dev.cs (Player, World, Money, Spawn, Board, Drive, Click, Shot, WaitFor, ...).
    ctx.Harmony patches, ctx.OnUpdate hooks and ctx.OnUnload cleanups are undone when the script runs again.
    Warnings and errors logged while the script runs are included in the output.

.EXAMPLE
    .\tools\cs.ps1 tools\scripts\selftest.cs
.EXAMPLE
    .\tools\cs.ps1 -Code "MoonPrototypeCatalog.Vehicles.Length"
.EXAMPLE
    .\tools\cs.ps1 -Code "Money = 1e6; return Money;"
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Position = 0)][string]$Path,
    [Parameter(Position = 1, ValueFromRemainingArguments = $true)][string[]]$ScriptArgs,
    [string]$Code,
    [int]$TimeoutSec = 180,
    # If the game isn't running, start it and load this save slot first (0: don't start it).
    [int]$Slot = 1,
    [string]$GameDir = "C:\Program Files (x86)\Steam\steamapps\common\Moon Sweepers"
)

$ErrorActionPreference = "Stop"
if (-not (Get-Process MoonSweepers -ErrorAction SilentlyContinue)) {
    if ($Slot -le 0) { throw "Moon Sweepers isn't running (use -Slot N to start it into a save)." }
    Write-Host "Starting Moon Sweepers into save slot $Slot..." -ForegroundColor Cyan
    & (Join-Path $PSScriptRoot "dev-relaunch.ps1") -NoBuild -LoadSlot $Slot -GameDir $GameDir | Out-Null
}
if ($Code) {
    $command = "eval64 " + [Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes($Code))
} elseif ($Path) {
    $full = (Resolve-Path $Path).Path
    $command = "cs `"$full`" " + ($ScriptArgs -join " ")
} else {
    throw "Give a script path or -Code."
}
& (Join-Path $PSScriptRoot "dev.ps1") $command -TimeoutSec $TimeoutSec -GameDir $GameDir
