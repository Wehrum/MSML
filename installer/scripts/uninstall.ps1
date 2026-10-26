<#
.SYNOPSIS
    Removes BepInEx and every installed mod from the Moon Sweepers folder, restoring the vanilla game.

.PARAMETER GameDir
    Moon Sweepers install folder. Auto-detected from Steam when omitted.

.PARAMETER Force
    Don't ask for confirmation.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
. (Join-Path $PSScriptRoot "common.ps1")

try {
    $GameDir = Resolve-GameDir $GameDir
    Assert-GameClosed

    # Exactly what the BepInEx zip adds to the game folder (BepInEx\ also holds mods, configs and generated files).
    $items = "BepInEx", "dotnet", "winhttp.dll", "doorstop_config.ini", ".doorstop_version", "changelog.txt" |
        ForEach-Object { Join-Path $GameDir $_ } | Where-Object { Test-Path $_ }
    if (-not $items) {
        Write-Host "Nothing to remove; $GameDir has no BepInEx install." -ForegroundColor Green
        exit 0
    }

    Write-Host "This removes BepInEx, the mod loader and your mod settings from:"
    $items | ForEach-Object { Write-Host "    $_" }
    if (-not $Force) {
        $answer = "$(Read-Host 'Continue? [y/N]')".Trim()
        if ($answer -notmatch '^(y|yes)$') { Write-Host "Cancelled."; exit 1 }
    }

    foreach ($path in $items) {
        Remove-Item -Recurse -Force $path
        Write-Host "Removed $path"
    }
    Write-Host "Done. Moon Sweepers is back to vanilla." -ForegroundColor Green
    $mods = Join-Path $GameDir "Mods"
    if (Test-Path $mods) { Write-Host "Your Mods folder was kept (the game ignores it without the mod loader): $mods" }
    exit 0
} catch {
    Write-Host "Uninstall failed: $($_.Exception.Message)" -ForegroundColor Red
    exit 1
}
