# Shared helpers for install.ps1 / uninstall.ps1. Dot-source this file.

$MoonSweepersAppId = 5335510
$StampFileName = "MoonSweepMods.install.json"

function Step([string]$msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Test-GameDir([string]$dir) {
    $dir -and (Test-Path (Join-Path $dir "GameAssembly.dll")) -and (Test-Path (Join-Path $dir "MoonSweepers.exe"))
}

function Get-SteamLibraries {
    $roots = @()
    foreach ($key in "HKCU:\Software\Valve\Steam", "HKLM:\SOFTWARE\WOW6432Node\Valve\Steam", "HKLM:\SOFTWARE\Valve\Steam") {
        $props = Get-ItemProperty $key -ErrorAction SilentlyContinue
        if ($props.SteamPath) { $roots += $props.SteamPath }
        if ($props.InstallPath) { $roots += $props.InstallPath }
    }
    $roots += "C:\Program Files (x86)\Steam"

    $libs = @()
    foreach ($root in $roots) {
        $root = $root -replace "/", "\"
        $libs += $root
        $vdf = Join-Path $root "steamapps\libraryfolders.vdf"
        if (Test-Path $vdf) {
            foreach ($m in [regex]::Matches((Get-Content $vdf -Raw), '"path"\s+"([^"]+)"')) {
                $libs += ($m.Groups[1].Value -replace "\\\\", "\")
            }
        }
    }
    $libs | Where-Object { $_ -and (Test-Path $_) } | ForEach-Object { (Resolve-Path $_).Path.TrimEnd("\") } | Sort-Object -Unique
}

function Find-GameDir {
    foreach ($lib in Get-SteamLibraries) {
        $manifest = Join-Path $lib "steamapps\appmanifest_$MoonSweepersAppId.acf"
        $installDir = "Moon Sweepers"
        if (Test-Path $manifest) {
            $m = [regex]::Match((Get-Content $manifest -Raw), '"installdir"\s+"([^"]+)"')
            if ($m.Success) { $installDir = $m.Groups[1].Value }
        }
        $dir = Join-Path $lib "steamapps\common\$installDir"
        if (Test-GameDir $dir) { return $dir }
    }
    return $null
}

# Returns the game folder from -GameDir, Steam auto-detection, or by asking the user.
function Resolve-GameDir([string]$GameDir) {
    if ($GameDir) {
        if (-not (Test-GameDir $GameDir)) { throw "Moon Sweepers not found at '$GameDir' (expected MoonSweepers.exe and GameAssembly.dll)." }
        return (Resolve-Path $GameDir).Path
    }
    $found = Find-GameDir
    if ($found) { return $found }

    Write-Host "Couldn't find Moon Sweepers automatically." -ForegroundColor Yellow
    Write-Host "In Steam: right-click Moon Sweepers > Manage > Browse local files, then copy that folder path."
    while ($true) {
        $answer = (Read-Host "Paste the Moon Sweepers folder path (or leave empty to cancel)").Trim('"', ' ')
        if (-not $answer) { throw "Cancelled." }
        if (Test-GameDir $answer) { return (Resolve-Path $answer).Path }
        Write-Host "That folder doesn't contain MoonSweepers.exe and GameAssembly.dll." -ForegroundColor Yellow
    }
}

function Assert-GameClosed {
    if (Get-Process MoonSweepers -ErrorAction SilentlyContinue) { throw "Moon Sweepers is running. Close the game and try again." }
}
