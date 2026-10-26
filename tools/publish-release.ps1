<#
.SYNOPSIS
    Builds and publishes an MSML release to GitHub (Wehrum/MSML). Players get it by running the install one-liner:
        irm https://github.com/Wehrum/MSML/releases/latest/download/get.ps1 | iex

.DESCRIPTION
    1. tools\build-installer.ps1 -Version <v>: installer zip, mod zips, get.ps1 and release.json (with SHA-256s and
       the game build this release was tested on, which is also recorded in compatibility.json).
    2. Commits compatibility.json, tags v<Version>, pushes (creating the GitHub repo on first use).
    3. Creates the GitHub release with every file in artifacts\release as an asset. "releases/latest/download/..."
       URLs then point at it, so the one-liner and the in-game update check pick it up.

    Requires the GitHub CLI (winget install GitHub.cli; gh auth login) and a clean working tree.

.PARAMETER Version
    Release version, e.g. 0.4.0 (semver; tagged v0.4.0).

.PARAMETER Notes
    Extra release notes (markdown), shown above the install instructions.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$Version,
    [string]$Notes = "",
    [switch]$SkipBuild,
    [switch]$AllowDirty
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot
$Repo = "Wehrum/MSML"
$Version = $Version.TrimStart("v")
$tag = "v$Version"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }
function Native([string]$exe, [string[]]$a) {
    & $exe @a
    if ($LASTEXITCODE -ne 0) { throw "Failed ($LASTEXITCODE): $exe $($a -join ' ')" }
}

$gh = (Get-Command gh -ErrorAction SilentlyContinue).Source
if (-not $gh) { $gh = "C:\Program Files\GitHub CLI\gh.exe" }
if (-not (Test-Path $gh)) { throw "GitHub CLI not found. Install it with: winget install GitHub.cli, then run: gh auth login" }
& $gh auth status *> $null
if ($LASTEXITCODE -ne 0) { throw "Not logged in to GitHub. Run: gh auth login" }

Push-Location $RepoRoot
try {
    if (-not $AllowDirty -and (git status --porcelain)) { throw "Commit or stash your changes first (or pass -AllowDirty)." }
    if (git tag --list $tag) { throw "Tag $tag already exists. Pick a new version." }

    # 1. Build ------------------------------------------------------------------------------------
    Step "Building MSML $Version"
    $buildArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $PSScriptRoot "build-installer.ps1"), "-Version", $Version)
    if ($SkipBuild) { $buildArgs += "-SkipBuild" }
    & powershell @buildArgs
    if ($LASTEXITCODE -ne 0) { throw "Build failed." }
    $release = Join-Path $RepoRoot "artifacts\release"
    $info = Get-Content (Join-Path $release "release.json") -Raw | ConvertFrom-Json

    # 2. Commit compatibility record, tag, push ----------------------------------------------------
    Step "Recording compatibility and tagging $tag"
    Native git @("add", "compatibility.json")
    if (git diff --cached --name-only) { Native git @("commit", "-q", "-m", "Release ${tag}: record tested game build") }
    Native git @("tag", "-a", $tag, "-m", "MSML $tag")

    & $gh repo view $Repo --json name *> $null
    if ($LASTEXITCODE -ne 0) {
        Step "Creating https://github.com/$Repo"
        Native $gh @("repo", "create", $Repo, "--public", "--description", "MSML - Moon Sweepers Mod Loader: BepInEx-based mod loader with an in-game Mods menu",
            "--source", ".", "--remote", "origin", "--push")
    }
    if (-not (git remote get-url origin 2>$null)) { Native git @("remote", "add", "origin", "https://github.com/$Repo.git") }
    $branch = (git rev-parse --abbrev-ref HEAD).Trim()
    Native git @("push", "-u", "origin", $branch)
    Native git @("push", "origin", $tag)

    # 3. GitHub release ------------------------------------------------------------------------------
    $tested = ($info.testedGames | Where-Object { $_.msml -eq $Version } | ForEach-Object { "Moon Sweepers v$($_.game) (Steam build $($_.steamBuild))" }) -join ", "
    $mods = ($info.mods | ForEach-Object { "- **$($_.name)** $($_.version): ``& ([scriptblock]::Create((irm https://github.com/$Repo/releases/latest/download/get.ps1))) -Mods $($_.name)``" }) -join "`n"
    $body = @"
$Notes

## Install or update

Close Moon Sweepers, then paste this into PowerShell:

``````powershell
irm https://github.com/$Repo/releases/latest/download/get.ps1 | iex
``````

Run the same command again any time to update, including after a Moon Sweepers update. Your mods and settings are kept.

## Compatibility

- Tested with: $tested
- Requires Unity $($info.requires.unity) / IL2CPP metadata v$($info.requires.metadata). The installer refuses unsupported game versions.

## Mods in this release

$mods

Manage mods in-game from **MODS** in the main menu.
"@
    $notesFile = Join-Path $env:TEMP "msml-notes-$Version.md"
    Set-Content $notesFile $body -Encoding utf8
    Step "Publishing GitHub release $tag"
    $assets = Get-ChildItem $release -File | ForEach-Object FullName
    Native $gh (@("release", "create", $tag, "--repo", $Repo, "--title", "MSML $tag", "--notes-file", $notesFile, "--latest") + $assets)
    Remove-Item $notesFile -ErrorAction SilentlyContinue

    Write-Host ""
    Write-Host "Published https://github.com/$Repo/releases/tag/$tag" -ForegroundColor Green
    Write-Host "Install/update command: irm https://github.com/$Repo/releases/latest/download/get.ps1 | iex" -ForegroundColor Green
} finally {
    Pop-Location
}
