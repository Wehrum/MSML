<#
    MSML (Moon Sweepers Mod Loader). The one command for installing, updating and uninstalling:

        irm https://github.com/Wehrum/MSML/releases/latest/download/get.ps1 | iex

    It shows a menu: install (if MSML isn't installed), or update / reinstall / uninstall (if it is). Downloads are
    checked against the release's SHA-256 checksums. Moon Sweepers is found through Steam. Administrator rights are
    only requested if Windows won't let us write to the game folder.
#>
param(
    # Skips the menu (used when re-launching elevated, and for testing): Install, Update, Reinstall, Uninstall.
    [ValidateSet("", "Install", "Update", "Reinstall", "Uninstall")][string]$Action = "",
    [string]$GameDir,
    # Install even if this game version isn't supported (you're on your own).
    [switch]$IgnoreCompatibility,
    # For testing: a local folder or http base URL holding the release files instead of GitHub.
    [string]$Source
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
[Net.ServicePointManager]::SecurityProtocol = [Net.ServicePointManager]::SecurityProtocol -bor [Net.SecurityProtocolType]::Tls12

$Repo = "Wehrum/MSML"
$GetUrl = "https://github.com/$Repo/releases/latest/download/get.ps1"
$SourceGiven = [bool]$Source -or [bool]$env:MSML_SOURCE
if (-not $Source) { $Source = if ($env:MSML_SOURCE) { $env:MSML_SOURCE } else { "https://github.com/$Repo/releases/latest/download" } }

function Say([string]$msg, [string]$color = "Gray") { Write-Host $msg -ForegroundColor $color }

function Fetch([string]$name, [string]$dest) {
    if ($Source -match '^https?://') { Invoke-WebRequest "$($Source.TrimEnd('/'))/$name" -OutFile $dest -UseBasicParsing }
    else { Copy-Item (Join-Path $Source $name) $dest }
}

function Test-Admin {
    (New-Object Security.Principal.WindowsPrincipal ([Security.Principal.WindowsIdentity]::GetCurrent())).IsInRole([Security.Principal.WindowsBuiltInRole]::Administrator)
}

function Restart-Elevated([string]$chosen) {
    Say "Windows needs administrator rights to change the game folder. Approve the prompt to continue." Yellow
    $parts = @("-Action $chosen")
    if ($GameDir) { $parts += "-GameDir '$GameDir'" }
    if ($IgnoreCompatibility) { $parts += "-IgnoreCompatibility" }
    if ($SourceGiven) { $parts += "-Source '$Source'" }
    $script = "([scriptblock]::Create((irm '$GetUrl')))"
    if ($Source -notmatch '^https://github\.com/') {
        $self = Join-Path ([IO.Path]::GetTempPath()) "msml-get.ps1"
        Fetch "get.ps1" $self
        $script = "([scriptblock]::Create((Get-Content '$self' -Raw)))"
    }
    $cmd = "& $script $($parts -join ' '); Read-Host 'Press Enter to close'"
    Start-Process powershell.exe -Verb RunAs -ArgumentList @("-NoProfile", "-ExecutionPolicy", "Bypass", "-Command", $cmd)
}

function Choose([string]$title, [string[]]$options) {
    Write-Host ""
    Say $title White
    for ($i = 0; $i -lt $options.Count; $i++) { Write-Host ("  [{0}] {1}" -f ($i + 1), $options[$i]) }
    while ($true) {
        $answer = "$(Read-Host 'Choose')".Trim()
        $n = 0
        if ([int]::TryParse($answer, [ref]$n) -and $n -ge 1 -and $n -le $options.Count) { return $options[$n - 1] }
        Say "Type a number from 1 to $($options.Count)." Yellow
    }
}

$work = Join-Path ([IO.Path]::GetTempPath()) ("MSML-" + [guid]::NewGuid().ToString("N"))
New-Item -ItemType Directory -Force $work | Out-Null
try {
    Write-Host ""
    Say "MSML - Moon Sweepers Mod Loader" Green
    Say "https://github.com/$Repo"

    # 1. Release info + helper scripts (small; the installer zip is only downloaded when installing) ---------
    Fetch "release.json" (Join-Path $work "release.json")
    $release = Get-Content (Join-Path $work "release.json") -Raw | ConvertFrom-Json
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    foreach ($f in $release.scripts) {
        $dest = Join-Path $work $f.file
        Fetch $f.file $dest
        if ((Get-FileHash $dest -Algorithm SHA256).Hash -ne $f.sha256) { throw "Download of $($f.file) failed its checksum. Try again." }
    }
    # Load helpers from text: dot-sourcing a .ps1 file is blocked under Windows' default execution policy.
    . ([scriptblock]::Create((Get-Content (Join-Path $work "common.ps1") -Raw)))

    # 2. Where things stand ---------------------------------------------------------------------------
    $GameDir = Resolve-GameDir $GameDir
    $game = Get-GameInfo $GameDir
    $stampPath = Join-Path $GameDir "BepInEx\$StampFileName"
    $stamp = if (Test-Path $stampPath) { Get-Content $stampPath -Raw | ConvertFrom-Json } else { $null }
    $installed = $stamp.packVersion
    $sameGame = $stamp -and $stamp.gameAssemblySha256 -eq $game.gameAssemblySha256

    Write-Host ""
    Say "Game:       Moon Sweepers $(Format-GameInfo $game)"
    Say "            $GameDir"
    Say "Installed:  $(if ($installed) { "MSML $installed" } else { 'not installed' })"
    Say "Latest:     MSML $($release.version)"
    if ($installed -and -not $sameGame) { Say "            Moon Sweepers was updated since MSML was installed." Yellow }

    # 3. What to do -------------------------------------------------------------------------------------
    if (-not $Action) {
        $upToDate = $installed -eq $release.version -and $sameGame
        $options = if (-not $installed) { @("Install MSML $($release.version)", "Quit") }
                   elseif ($upToDate) { @("Reinstall MSML $installed", "Uninstall MSML", "Quit") }
                   else { @("Update to MSML $($release.version)", "Uninstall MSML", "Quit") }
        $picked = Choose "What would you like to do?" $options
        $verb = ($picked -split ' ')[0]
        if ($verb -eq "Quit") { Say "Nothing changed."; return }
        if ($verb -eq "Uninstall") {
            Say "This removes MSML and BepInEx from the game. Your Mods folder is kept." Yellow
            if ("$(Read-Host 'Uninstall? [y/N]')".Trim() -notmatch '^(y|yes)$') { Say "Nothing changed."; return }
        }
        $Action = $verb
    }

    Assert-GameClosed
    $probe = Join-Path $GameDir ".msml-write-test"
    try { [IO.File]::WriteAllText($probe, "ok"); Remove-Item $probe -Force }
    catch {
        if (Test-Admin) { throw "Can't write to '$GameDir' even as administrator: $($_.Exception.Message)" }
        Restart-Elevated $Action
        return
    }

    # 4a. Uninstall --------------------------------------------------------------------------------------
    if ($Action -eq "Uninstall") {
        & powershell.exe -NoProfile -ExecutionPolicy Bypass -File (Join-Path $work "uninstall.ps1") -GameDir $GameDir -Force
        if ($LASTEXITCODE -ne 0) { throw "Uninstall failed." }
        Write-Host ""
        Say "MSML is uninstalled. Run the same command again any time to reinstall." Green
        return
    }

    # 4b. Install / update / reinstall -------------------------------------------------------------------
    if ($release.requires) {
        $engineOk = $game.metadata -eq $release.requires.metadata -and "$($game.unity)".StartsWith("$($release.requires.unity)")
        if (-not $engineOk -and -not $IgnoreCompatibility) {
            throw "This version of Moon Sweepers (Unity $($game.unity), metadata v$($game.metadata)) isn't supported yet. MSML $($release.version) " +
                "needs Unity $($release.requires.unity) / metadata v$($release.requires.metadata). The game probably got a big update; " +
                "run this command again once a new MSML release is out (https://github.com/$Repo/releases)."
        }
        $tested = @($release.testedGames | Where-Object { $_.gameAssemblySha256 -eq $game.gameAssemblySha256 })
        if ($tested) { Say "This game build is tested with MSML $($tested[0].msml)." Green }
        else { Say "This exact game build hasn't been tested with MSML $($release.version) yet. It usually works; if mods misbehave, run this command again later." Yellow }
    }

    $zip = Join-Path $work $release.installer.file
    Say "Downloading $($release.installer.file) ($([math]::Round($release.installer.bytes / 1MB, 1)) MB)..." Cyan
    Fetch $release.installer.file $zip
    if ((Get-FileHash $zip -Algorithm SHA256).Hash -ne $release.installer.sha256) { throw "Download is corrupt or was tampered with (SHA-256 mismatch). Try again." }
    $extract = Join-Path $work "pack"
    [IO.Compression.ZipFile]::ExtractToDirectory($zip, $extract)
    $scripts = Join-Path (Get-ChildItem $extract -Directory | Select-Object -First 1).FullName "scripts"
    $installArgs = @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", (Join-Path $scripts "install.ps1"), "-GameDir", $GameDir)
    if ($IgnoreCompatibility) { $installArgs += "-IgnoreCompatibility" }
    if ($Action -eq "Reinstall") { $installArgs += "-ForceRegenerate" }
    & powershell.exe @installArgs
    if ($LASTEXITCODE -ne 0) { throw "The installer reported an error (see above)." }

    Write-Host ""
    Say "Done! Launch Moon Sweepers from Steam." Green
    Say "To add a mod: download its .zip, then in the main menu choose MODS > ADD MOD (or extract it into $GameDir\Mods)."
    Say "To update or uninstall later, run the same command again."
} catch {
    Write-Host ""
    Say "MSML: $($_.Exception.Message)" Red
    if ($_.Exception.GetBaseException() -is [UnauthorizedAccessException]) { Say "Try running PowerShell as administrator." Yellow }
} finally {
    Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
}
