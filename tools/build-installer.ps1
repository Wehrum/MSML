<#
.SYNOPSIS
    Builds the player-facing installer (artifacts\MoonSweepMods-<Version>.zip) and the example mods
    (artifacts\mods\<Mod>-<version>.zip).

.DESCRIPTION
    The installer sets up BepInEx (patched for this game) and the mod loader, which adds MODS to the main menu and
    loads mods from <game>\Mods. It contains no gameplay mods. Mods are separate zips that players add with
    ADD MOD in-game, or by extracting them into the Mods folder.

    1. Builds/reuses the tools payload (tools\build-tools.ps1).
    2. Builds the mod loader (src\ModLoader + src\ModLoader.Patcher) into the payload.
    3. Adds Install.bat / Uninstall.bat / README.txt (installer\) and third-party notices, and zips it.
    4. Builds every project under examples\ and zips each one as a drop-in mod.

    Players don't need .NET, git, or this repo. Game-derived files (interop/dummy assemblies, Unity libraries) are
    never included. The installer generates them on the player's machine.

    Compiling needs the interop assemblies from your own game install, so run tools\setup-modding.ps1 first.

.PARAMETER Version
    Pack version used in the zip name and pack.json. Defaults to the latest git tag, or today's date.

.PARAMETER SkipBuild
    Reuse previously built Cpp2IL/Il2CppInterop instead of fetching/rebuilding them.

.PARAMETER GameDir
    Game folder to compile against, if not the default from Directory.Build.props.
#>
[CmdletBinding()]
param(
    [string]$Version,
    [switch]$SkipBuild,
    [string]$GameDir
)

$ErrorActionPreference = "Stop"
$RepoRoot  = Split-Path -Parent $PSScriptRoot
$Artifacts = Join-Path $RepoRoot "artifacts"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

# Builds a project with deployment redirected into $DeployRoot (same layout as the game folder).
function Build-Project([string]$Project, [string]$DeployRoot) {
    $buildArgs = @("build", $Project, "-c", "Release", "-nologo", "-v", "m", "-p:DeployToGame=true", "-p:GameDeployRoot=$DeployRoot")
    if ($GameDir) { $buildArgs += "-p:GameDir=$GameDir" }
    & dotnet @buildArgs | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Build failed: $Project" }
}

# ZipFile.CreateFromDirectory under Windows PowerShell writes "\" separators, which breaks the zip spec; add entries by hand.
function New-Zip([string]$SourceDir, [string]$ZipPath, [string]$RootName) {
    Add-Type -AssemblyName System.IO.Compression, System.IO.Compression.FileSystem
    Remove-Item -Force $ZipPath -ErrorAction SilentlyContinue
    New-Item -ItemType Directory -Force (Split-Path -Parent $ZipPath) | Out-Null
    $archive = [IO.Compression.ZipFile]::Open($ZipPath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($file in Get-ChildItem $SourceDir -Recurse -File) {
            $entryName = "$RootName/" + $file.FullName.Substring($SourceDir.Length + 1).Replace("\", "/")
            [void][IO.Compression.ZipFileExtensions]::CreateEntryFromFile($archive, $file.FullName, $entryName, [IO.Compression.CompressionLevel]::Optimal)
        }
    } finally { $archive.Dispose() }
}

if (-not $Version) {
    $Version = & {
        $ErrorActionPreference = "Continue"   # no tags yet is fine; don't turn git's stderr into an error
        git -C $RepoRoot describe --tags --abbrev=0 2>$null
    }
    if ($LASTEXITCODE -ne 0 -or -not $Version) { $Version = Get-Date -Format "yyyy.MM.dd" }
    $Version = "$Version".TrimStart("v")
}
$packName = "MSML-$Version"

# 1. Tools payload ------------------------------------------------------------------------------
$toolsPayload = & (Join-Path $PSScriptRoot "build-tools.ps1") -SkipBuild:$SkipBuild

# 2. Stage + mod loader -------------------------------------------------------------------------
$stage = Join-Path $Artifacts "installer\$packName"
$payload = Join-Path $stage "payload"
$deploy = Join-Path $Artifacts "deploy"
Step "Staging $stage"
Remove-Item -Recurse -Force $stage, $deploy -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $stage | Out-Null
Copy-Item -Recurse (Join-Path $RepoRoot "installer\*") $stage -Exclude "get.ps1"
Copy-Item -Recurse $toolsPayload $payload

Step "Building the mod loader"
Build-Project (Join-Path $RepoRoot "src\ModLoader.Patcher\ModLoader.Patcher.csproj") $deploy
Build-Project (Join-Path $RepoRoot "src\ModLoader\ModLoader.csproj") $deploy
Copy-Item -Recurse (Join-Path $deploy "BepInEx\plugins") (Join-Path $payload "plugins")
Copy-Item -Recurse (Join-Path $deploy "BepInEx\patchers") (Join-Path $payload "patchers")
$loaderVersion = (Get-Item (Join-Path $payload "plugins\MoonSweepMods.ModLoader\MoonSweepMods.ModLoader.dll")).VersionInfo.ProductVersion -replace '\+.*$', ''

$pack = Get-Content (Join-Path $payload "pack.json") -Raw | ConvertFrom-Json
$pack.version = $Version
$pack.name = "MSML (Moon Sweepers Mod Loader)"
$pack | Add-Member -Force NoteProperty modLoader $loaderVersion
$pack.PSObject.Properties.Remove("plugins")

# Game compatibility: record the game this release was built and tested against (compatibility.json, committed).
. ([scriptblock]::Create((Get-Content (Join-Path $RepoRoot "installer\scripts\common.ps1") -Raw)))
$testGameDir = Resolve-GameDir $(if ($GameDir) { $GameDir } else { $null })
$game = Get-GameInfo $testGameDir
Step "Built against Moon Sweepers $(Format-GameInfo $game)"
$compatPath = Join-Path $RepoRoot "compatibility.json"
$compat = Get-Content $compatPath -Raw | ConvertFrom-Json
if ($game.metadata -ne $compat.requires.metadata -or -not "$($game.unity)".StartsWith("$($compat.requires.unity)")) {
    throw "Your game is Unity $($game.unity) / metadata v$($game.metadata), but compatibility.json requires Unity $($compat.requires.unity) / metadata v$($compat.requires.metadata). Update the Il2CppInterop patch and compatibility.json first."
}
$tested = @($compat.tested | Where-Object { -not ($_.msml -eq $Version -and $_.gameAssemblySha256 -eq $game.gameAssemblySha256) })
$tested = @([pscustomobject][ordered]@{
    msml = $Version; game = $game.version; steamBuild = $game.steamBuild; gameAssemblySha256 = $game.gameAssemblySha256
    date = (Get-Date -Format "yyyy-MM-dd")
}) + $tested
$compat.tested = $tested
$compat | ConvertTo-Json -Depth 5 | Set-Content $compatPath -Encoding utf8
$pack | Add-Member -Force NoteProperty requires ([ordered]@{ unity = $compat.requires.unity; metadata = $compat.requires.metadata })
$pack | Add-Member -Force NoteProperty testedGames @($tested | Select-Object msml, game, steamBuild, gameAssemblySha256)
$pack | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $payload "pack.json") -Encoding utf8

# 3. Notices + installer zip --------------------------------------------------------------------
$licenses = Join-Path $stage "licenses"
New-Item -ItemType Directory -Force $licenses | Out-Null
Copy-Item (Join-Path $PSScriptRoot "il2cppinterop\Class_108_0.cs") $licenses
@"
THIRD-PARTY NOTICES
===================

This pack redistributes the following open-source software, unmodified unless noted.

BepInEx $($pack.bepinex) (payload\BepInEx.zip, installed into the game folder)
  License: LGPL-2.1. https://github.com/BepInEx/BepInEx
  Includes UnityDoorstop (LGPL-2.1, https://github.com/NeighTools/UnityDoorstop),
  HarmonyX (MIT, https://github.com/BepInEx/HarmonyX) and the .NET runtime (MIT, https://github.com/dotnet/runtime).

Il2CppInterop, commit $($pack.il2cppInteropCommit), MODIFIED (payload\core\Il2CppInterop.*.dll, payload\tools\Il2CppInterop.CLI.exe)
  License: LGPL-3.0. https://github.com/BepInEx/Il2CppInterop
  Modification: adds Runtime\VersionSpecific\Class\Class_108_0.cs (Unity 6000.6 Il2CppClass layout).
  The full source of that file is in licenses\Class_108_0.cs. Build it by adding the file to the upstream commit above.
  Il2CppInterop.CLI.exe is a self-contained .NET build that bundles the .NET runtime (MIT) and its MIT-licensed
  dependencies (AsmResolver, Mono.Cecil, Iced, System.CommandLine, Microsoft.Extensions.*).

TerraFX.Interop.Windows (payload\core\TerraFX.Interop.Windows.dll)
  License: MIT. https://github.com/terrafx/terrafx.interop.windows

Cpp2IL, commit $($pack.cpp2ilCommit) (payload\tools\Cpp2IL.exe)
  License: MIT. https://github.com/SamboyCoding/Cpp2IL
  Self-contained .NET build that bundles the .NET runtime (MIT) and Cpp2IL's MIT-licensed dependencies.

The installer generates interop assemblies from your own copy of Moon Sweepers and downloads Unity's base libraries
from unity.bepinex.dev. Neither is included in this pack.
"@ | Set-Content (Join-Path $stage "THIRD-PARTY-NOTICES.txt") -Encoding utf8

$zip = Join-Path $Artifacts "$packName.zip"
Step "Creating $zip"
New-Zip $stage $zip $packName

# 4. Example mods as drop-in zips ---------------------------------------------------------------
$modsOut = Join-Path $Artifacts "mods"
Remove-Item -Recurse -Force $modsOut -ErrorAction SilentlyContinue
$examples = @(Get-ChildItem (Join-Path $RepoRoot "examples") -Filter *.csproj -Recurse -ErrorAction SilentlyContinue)
foreach ($proj in $examples) {
    Step "Building example mod $($proj.BaseName)"
    Build-Project $proj.FullName $deploy
}
$modZips = @()
foreach ($dir in Get-ChildItem (Join-Path $deploy "Mods") -Directory -ErrorAction SilentlyContinue) {
    $manifest = Join-Path $dir.FullName "mod.json"
    $ver = if (Test-Path $manifest) { (Get-Content $manifest -Raw | ConvertFrom-Json).version } else { "" }
    if (-not $ver) { $ver = (Get-ChildItem $dir.FullName -Filter *.dll | Select-Object -First 1).VersionInfo.ProductVersion -replace '\+.*$', '' }
    $modZip = Join-Path $modsOut "$($dir.Name)-$ver.zip"
    New-Zip $dir.FullName $modZip $dir.Name
    $modZips += $modZip
}
Remove-Item -Recurse -Force $deploy

# 5. GitHub release folder: get.ps1 (the one-liner), the installer, mods, and release.json with checksums -----------
$release = Join-Path $Artifacts "release"
Step "Assembling release files in $release"
Remove-Item -Recurse -Force $release -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force $release | Out-Null
Copy-Item (Join-Path $RepoRoot "installer\get.ps1") $release
Copy-Item $zip $release
$scriptEntries = @(foreach ($s in "common.ps1", "uninstall.ps1") {
    $src = Join-Path $RepoRoot "installer\scripts\$s"
    Copy-Item $src $release
    [ordered]@{ file = $s; sha256 = (Get-FileHash $src -Algorithm SHA256).Hash }
})
$modEntries = @(foreach ($m in $modZips) {
    Copy-Item $m $release
    $file = Split-Path -Leaf $m
    $match = [regex]::Match($file, '^(.*)-([0-9][^-]*)\.zip$')
    [ordered]@{ name = $match.Groups[1].Value; version = $match.Groups[2].Value; file = $file; sha256 = (Get-FileHash $m -Algorithm SHA256).Hash; bytes = (Get-Item $m).Length }
})
[ordered]@{
    version   = $Version
    published = (Get-Date).ToUniversalTime().ToString("s") + "Z"
    installer = [ordered]@{ file = (Split-Path -Leaf $zip); sha256 = (Get-FileHash $zip -Algorithm SHA256).Hash; bytes = (Get-Item $zip).Length }
    scripts   = $scriptEntries
    requires  = $pack.requires
    testedGames = $pack.testedGames
    mods      = $modEntries
} | ConvertTo-Json -Depth 5 | Set-Content (Join-Path $release "release.json") -Encoding utf8

Write-Host ("Built {0} ({1:N1} MB): BepInEx + mod loader {2}" -f $zip, ((Get-Item $zip).Length / 1MB), $loaderVersion) -ForegroundColor Green
foreach ($m in $modZips) { Write-Host "Built mod $m" -ForegroundColor Green }
