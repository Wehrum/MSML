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

<#
  Identifies the installed game: version string (as shown in the main menu), Steam build id, Unity version,
  IL2CPP metadata version, and a SHA-256 of GameAssembly.dll (changes with every game update).
  MSML needs a matching Unity/metadata version (its patched Il2CppInterop is built for them); other game updates
  usually work after the installer regenerates the interop assemblies.
#>
function Get-GameInfo([string]$GameDir) {
    $info = [ordered]@{ version = $null; steamBuild = $null; unity = $null; metadata = $null; gameAssemblySha256 = $null }
    try {
        $ggm = [IO.File]::ReadAllBytes((Join-Path $GameDir "MoonSweepers_Data\globalgamemanagers"))
        $text = [Text.Encoding]::GetEncoding(28591).GetString($ggm)   # Latin-1: one char per byte
        $info.unity = [regex]::Match($text.Substring(0, [Math]::Min(512, $text.Length)), '(\d{4}\.\d+\.\d+)[abfp]\d+').Groups[1].Value
        # PlayerSettings.bundleVersion: a length-prefixed string such as "1.0.4". Prefer the most specific match.
        $best = $null
        foreach ($m in [regex]::Matches($text, '([\x03-\x10])\x00\x00\x00(\d+\.\d+(?:\.\d+){0,2})')) {
            $v = $m.Groups[2].Value
            if ([int][char]$m.Groups[1].Value -ne $v.Length -or $v -eq $info.unity) { continue }
            if (-not $best -or $v.Split('.').Count -gt $best.Split('.').Count) { $best = $v }
        }
        $info.version = $best
    } catch { }
    try {
        $meta = Join-Path $GameDir "MoonSweepers_Data\il2cpp_data\Metadata\global-metadata.dat"
        $fs = [IO.File]::OpenRead($meta); $buf = New-Object byte[] 8; [void]$fs.Read($buf, 0, 8); $fs.Dispose()
        $info.metadata = [BitConverter]::ToInt32($buf, 4)
    } catch { }
    try {
        $manifest = Join-Path (Split-Path (Split-Path $GameDir)) "appmanifest_$MoonSweepersAppId.acf"
        if (Test-Path $manifest) { $info.steamBuild = [regex]::Match((Get-Content $manifest -Raw), '"buildid"\s+"(\d+)"').Groups[1].Value }
    } catch { }
    $info.gameAssemblySha256 = (Get-FileHash (Join-Path $GameDir "GameAssembly.dll") -Algorithm SHA256).Hash
    return [pscustomobject]$info
}

function Format-GameInfo($g) {
    $parts = @()
    if ($g.version) { $parts += "v$($g.version)" }
    if ($g.steamBuild) { $parts += "Steam build $($g.steamBuild)" }
    if ($g.unity) { $parts += "Unity $($g.unity)" }
    if ($g.metadata) { $parts += "metadata v$($g.metadata)" }
    return ($parts -join ", ")
}
