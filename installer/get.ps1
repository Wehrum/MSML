<#
    MSML (Moon Sweepers Mod Loader) installer / updater.

    Install or update (paste into PowerShell):
        irm https://github.com/Wehrum/MSML/releases/latest/download/get.ps1 | iex

    With options:
        & ([scriptblock]::Create((irm https://github.com/Wehrum/MSML/releases/latest/download/get.ps1))) -Force
        ... -Uninstall      remove MSML (your Mods folder is kept)
        ... -Force          reinstall even if up to date (also installs on an unsupported game version)
        ... -GameDir "D:\SteamLibrary\steamapps\common\Moon Sweepers"

    Downloads the latest release from GitHub, checks it against the release's SHA-256 checksums, finds Moon Sweepers
    through Steam, and runs the bundled installer. Running it again updates to the newest release.
#>
param(
    [switch]$Uninstall,
    [switch]$Force,
    [string]$GameDir,
    # For testing: a local folder (or http base URL) holding the release files instead of GitHub.
    [string]$Source
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repo = "Wehrum/MSML"
$GetUrl = "https://github.com/$Repo/releases/latest/download/get.ps1"
$SourceGiven = [bool]$Source -or [bool]$env:MSML_SOURCE
if (-not $Source) { $Source = if ($env:MSML_SOURCE) { $env:MSML_SOURCE } else { "https://github.com/$Repo/releases/latest/download" } }

function Say([string]$msg, [string]$color = "Cyan") { Write-Host $msg -ForegroundColor $color }

function Fetch([string]$name, [string]$dest) {
    if ($Source -match '^https?://') {
        Invoke-WebRequest "$($Source.TrimEnd('/'))/$name" -OutFile $dest -UseBasicParsing
    } else {
        Copy-Item (Join-Path $Source $name) $dest
    }
}

function Test-Admin {
    $id = [Security.Principal.WindowsIdentity]::GetCurrent()
    (New-Object Security.Principal.WindowsPrincipal $id).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-Elevated {
    Say "Windows needs administrator rights to write to the game folder. Approve the prompt to continue." Yellow
    $parts = @()
    if ($Uninstall) { $parts += "-Uninstall" }
    if ($Force) { $parts += "-Force" }
    if ($GameDir) { $parts += "-GameDir '$GameDir'" }
    if ($SourceGiven) { $parts += "-Source '$Source'" }
    $cmd = "& ([scriptblock]::Create((irm '$GetUrl'))) $($parts -join ' '); Read-Host 'Press Enter to close'"
    if ($Source -notmatch '^https://github\.com/') {
        # Testing from a local source: re-run the same script text.
        $self = Join-Path ([IO.Path]::GetTempPath()) "msml-get.ps1"
        Fetch "get.ps1" $self
        $cmd = "& '$self' $($parts -join ' '); Read-Host 'Press Enter to close'"
    }
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $cmd)
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("MSML-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    Say "MSML - Moon Sweepers Mod Loader" Green

    # 1. Release info -------------------------------------------------------------------------------
    Fetch "release.json" (Join-Path $work "release.json")
    $release = Get-Content (Join-Path $work "release.json") -Raw | ConvertFrom-Json
    Say "Latest release: $($release.version)"

    # 2. Helper scripts (small; the big installer zip is only downloaded when something needs installing) ----
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($f in $release.scripts) {
        $dest = Join-Path $work $f.file
        Fetch $f.file $dest
        if ((Get-FileHash $dest -Algorithm SHA256).Hash -ne $f.sha256) { throw "Download of $($f.file) failed its checksum. Try again." }
    }
    # Load helpers from text: dot-sourcing a .ps1 file is blocked under Windows' default execution policy.
    . ([scriptblock]::Create((Get-Content (Join-Path $work "common.ps1") -Raw)))

    function Get-Installer {
        $zip = Join-Path $work $release.installer.file
        Say "Downloading $($release.installer.file) ($([math]::Round($release.installer.bytes / 1MB, 1)) MB)..."
        Fetch $release.installer.file $zip
        if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $release.installer.sha256) { throw "Download is corrupt or was tampered with (SHA-256 mismatch). Try again." }
        $extract = Join-Path $work "pack"
        [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extract)
        return Join-Path (Get-ChildItem $extract -Directory | Select-Object -First 1).FullName "scripts"
    }

    # 3. Game folder, compatibility, permissions ----------------------------------------------------
    $GameDir = Resolve-GameDir $GameDir
    Say "Game folder: $GameDir"
    Assert-GameClosed
    $game = Get-GameInfo $GameDir
    Say "Game: Moon Sweepers $(Format-GameInfo $game)"
    if ($release.requires) {
        $engineOk = $game.metadata -eq $release.requires.metadata -and "$($game.unity)".StartsWith("$($release.requires.unity)")
        if (-not $engineOk) {
            if (-not $Force) {
                throw "This version of Moon Sweepers (Unity $($game.unity), metadata v$($game.metadata)) isn't supported yet. MSML $($release.version) " +
                    "needs Unity $($release.requires.unity) / metadata v$($release.requires.metadata). The game probably got a big update; " +
                    "run this command again once a new MSML release is out (https://github.com/$Repo/releases)."
            }
            Say "Unsupported game engine version; installing anyway because of -Force." Yellow
        }
        $testedHere = @($release.testedGames | Where-Object { $_.gameAssemblySha256 -eq $game.gameAssemblySha256 })
        if ($testedHere) { Say "This game build is tested with MSML $($testedHere[0].msml)." Green }
        else { Say "This exact game build hasn't been tested with MSML yet. It usually works; if anything breaks, run this command again later for an update." Yellow }
    }
    $probe = Join-Path $GameDir ".msml-write-test"
    try { [IO.File]::WriteAllText($probe, "ok"); Remove-Item $probe -Force }
    catch {
        if (Test-Admin) { throw "Can't write to '$GameDir' even as administrator: $($_.Exception.Message)" }
        Restart-Elevated
        return
    }

    $stampPath = Join-Path $GameDir "BepInEx\$StampFileName"
    $stamp = if (Test-Path $stampPath) { Get-Content $stampPath -Raw | ConvertFrom-Json } else { $null }
    $installed = $stamp.packVersion
    $sameGame = $stamp -and $stamp.gameAssemblySha256 -eq $game.gameAssemblySha256

    # 4. Uninstall -------------------------------------------------------------------------------------
    if ($Uninstall) {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $work "uninstall.ps1") -GameDir $GameDir -Force
        if ($LASTEXITCODE -ne 0) { throw "Uninstall failed." }
        return
    }

    # 5. Install / update -----------------------------------------------------------------------------
    if ($installed -eq $release.version -and $sameGame -and -not $Force) {
        Say "MSML $installed is already installed and up to date." Green
    } else {
        Say ($(if (-not $installed) { "Installing MSML $($release.version)" }
               elseif ($installed -ne $release.version) { "Updating MSML $installed -> $($release.version)" }
               elseif (-not $sameGame) { "Moon Sweepers was updated since MSML was installed; refreshing MSML $installed for the new game version" }
               else { "Reinstalling MSML $installed" }))
        $scripts = Get-Installer
        $installArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $scripts "install.ps1"), "-GameDir", $GameDir)
        if ($Force) { $installArgs += "-IgnoreCompatibility" }
        & powershell.exe @installArgs
        if ($LASTEXITCODE -ne 0) { throw "The installer reported an error (see above)." }
    }

    Write-Host ""
    Say "Done! Launch Moon Sweepers from Steam." Green
    Say "To add a mod: download its .zip, then in the main menu choose MODS > ADD MOD (or extract it into $GameDir\Mods)."
    Say "To update later, run the same command again."
} catch {
    Write-Host ""
    Say "MSML install failed: $($_.Exception.Message)" Red
    if ($_.Exception.GetBaseException() -is [UnauthorizedAccessException]) { Say "Try running PowerShell as administrator." Yellow }
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
