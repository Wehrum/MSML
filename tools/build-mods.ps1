<#
.SYNOPSIS
    Builds every mod under examples\ into a drop-in zip (artifacts\mods), and with -Sync copies them into the repo's
    mods\ folder (with a generated mods\README.md) so players can download them from GitHub.

.DESCRIPTION
    A mod zip holds one folder named after the mod, containing its DLL, mod.json and preview.png. A zip is only
    replaced in mods\ when its contents changed, so rebuilding without changes leaves the repo untouched. Older
    versions of the same mod are removed.

    Players add a zip in-game with MODS > ADD MOD, or extract it into <game>\Mods\.

.PARAMETER Sync
    Update the repo's mods\ folder. Prints whether anything changed.

.PARAMETER GameDir
    Game folder to compile against, if not the default from Directory.Build.props.
#>
[CmdletBinding()]
param(
    [switch]$Sync,
    [string]$GameDir
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot "artifacts"
$ModsOut = Join-Path $Artifacts "mods"
$RepoMods = Join-Path $RepoRoot "mods"
$Repo = "Wehrum/MSML"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# ZipFile.CreateFromDirectory under Windows PowerShell writes "\" separators, which breaks the zip spec; add entries by hand.
function New-Zip([string]$SourceDir, [string]$ZipPath, [string]$RootName) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    Remove-Item -Force $ZipPath -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force (Split-Path -Parent $ZipPath) | Out-Null
    $archive = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $SourceDir -Recurse -File | Sort-Object FullName) {
            $entryName = "$RootName/" + $file.FullName.Substring($SourceDir.Length + 1).Replace("\", "/")
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
}

# Content fingerprint of a zip (entry names + file hashes), ignoring timestamps.
function Get-ZipDigest([string]$ZipPath) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $sha = [Security.Cryptography.SHA256]::Create()
    $archive = [IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $parts = foreach ($e in $archive.Entries | Sort-Object FullName) {
            if ($e.FullName.EndsWith("/")) { continue }
            $s = $e.Open(); try { "$($e.FullName)=$([BitConverter]::ToString($sha.ComputeHash($s)))" } finally { $s.Dispose() }
        }
        return ($parts -join "|")
    } finally { $archive.Dispose() }
}

# 1. Build ------------------------------------------------------------------------------------------
$deploy = Join-Path $Artifacts "deploy-mods"
Remove-Item -Recurse -Force $deploy, $ModsOut -ErrorAction SilentlyContinue
$projects = @(Get-ChildItem (Join-Path $RepoRoot "examples") -Filter *.csproj -Recurse -ErrorAction SilentlyContinue)
foreach ($proj in $projects) {
    Step "Building mod $($proj.BaseName)"
    $buildArgs = @("build", $proj.FullName, "-c", "Release", "-nologo", "-v", "m", "-p:DeployToGame=true", "-p:GameDeployRoot=$deploy")
    if ($GameDir) { $buildArgs += "-p:GameDir=$GameDir" }
    & dotnet @buildArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $($proj.FullName)" }
}

$mods = @()
foreach ($dir in Get-ChildItem (Join-Path $deploy "Mods") -Directory -ErrorAction SilentlyContinue) {
    $manifestPath = Join-Path $dir.FullName "mod.json"
    $manifest = if (Test-Path $manifestPath) { Get-Content $manifestPath -Raw | ConvertFrom-Json } else { $null }
    $ver = $manifest.version
    if (-not $ver) { $ver = (Get-ChildItem $dir.FullName -Filter *.dll | Select-Object -First 1).VersionInfo.ProductVersion -replace '\+.*$', '' }
    $zip = Join-Path $ModsOut "$($dir.Name)-$ver.zip"
    New-Zip $dir.FullName $zip $dir.Name
    $mods += [pscustomobject]@{ Key = $dir.Name; Version = $ver; Zip = $zip; Name = $(if ($manifest.name) { $manifest.name } else { $dir.Name });
        Description = "$($manifest.description)"; Author = "$($manifest.author)" }
    Write-Host "Built mod $zip" -ForegroundColor Green
}
Remove-Item -Recurse -Force $deploy

if (-not $Sync) { return }

# 2. Sync into the repo's mods\ folder ------------------------------------------------------------------
New-Item -ItemType Directory -Force $RepoMods | Out-Null
$changed = $false
foreach ($m in $mods) {
    $target = Join-Path $RepoMods (Split-Path -Leaf $m.Zip)
    $same = (Test-Path $target) -and ((Get-ZipDigest $target) -eq (Get-ZipDigest $m.Zip))
    if (-not $same) { Copy-Item -Force $m.Zip $target; $changed = $true; Write-Host "Updated mods\$(Split-Path -Leaf $target)" -ForegroundColor Green }
    # Remove other versions of this mod.
    foreach ($old in Get-ChildItem $RepoMods -Filter "$($m.Key)-*.zip" | Where-Object { $_.Name -ne (Split-Path -Leaf $target) }) {
        Remove-Item -Force $old.FullName; $changed = $true; Write-Host "Removed old mods\$($old.Name)"
    }
}
# Remove zips of mods that no longer exist.
foreach ($zip in Get-ChildItem $RepoMods -Filter *.zip) {
    if (-not ($mods | Where-Object { $zip.Name -eq (Split-Path -Leaf $_.Zip) })) { Remove-Item -Force $zip.FullName; $changed = $true; Write-Host "Removed mods\$($zip.Name)" }
}

$rows = foreach ($m in $mods | Sort-Object Name) {
    $file = Split-Path -Leaf $m.Zip
    $desc = ($m.Description -split "`n")[0].Replace("|", "\|")
    "| **$($m.Name)** | $($m.Version) | $desc | [Download](https://github.com/$Repo/raw/main/mods/$file) |"
}
$readme = @"
# Mods

Drop-in mods for [MSML](../README.md). To install one:

1. Install MSML first (see the [main README](../README.md)).
2. Download the mod's ``.zip`` below.
3. In Moon Sweepers, open **MODS > ADD MOD** and pick the zip (or extract it into ``<game>\Mods\``).
4. Restart the game (**RESTART TO APPLY**).

> [!WARNING]
> These mods are experimental and only lightly tested. They can break your game or corrupt your save or moon.
> Back up your saves (``%USERPROFILE%\AppData\LocalLow\DragDropGames\MoonSweepers``) before using them. You use them at your own risk.

| Mod | Version | Description | |
|---|---|---|---|
$($rows -join "`n")

This file is generated by ``tools/build-mods.ps1 -Sync``; don't edit it by hand.
"@
$readmePath = Join-Path $RepoMods "README.md"
$oldReadme = if (Test-Path $readmePath) { Get-Content $readmePath -Raw } else { "" }
if ($oldReadme.Trim() -ne $readme.Trim()) { Set-Content $readmePath $readme -Encoding utf8; $changed = $true }

if ($changed) { Write-Host "mods\ changed." -ForegroundColor Green } else { Write-Host "mods\ is up to date." }
