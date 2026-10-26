<#
.SYNOPSIS
    Removes BepInEx (and every installed mod) from the Moon Sweepers folder, restoring the vanilla game.
    Same as the Uninstall.bat shipped in the installer.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$Force
)

& (Join-Path (Split-Path -Parent $PSScriptRoot) "installer\scripts\uninstall.ps1") @PSBoundParameters
exit $LASTEXITCODE
