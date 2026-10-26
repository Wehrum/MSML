<#
.SYNOPSIS
    Installs BepInEx (patched for Moon Sweepers) and the mod loader into the game folder.

.DESCRIPTION
    Used both by the player-facing Install.bat and by tools\setup-modding.ps1. Expects a payload folder laid out as:

        payload\pack.json                      pack metadata (written by tools\build-tools.ps1 / build-installer.ps1)
        payload\BepInEx.zip                    BepInEx Bleeding Edge (IL2CPP x64)
        payload\core\*.dll                     patched Il2CppInterop runtime, copied over BepInEx\core
        payload\tools\Cpp2IL.exe               self-contained tools that generate the interop assemblies locally
        payload\tools\Il2CppInterop.CLI.exe
        payload\plugins\<Name>\*.dll           optional: mod loader plugin  -> BepInEx\plugins
        payload\patchers\<Name>\*.dll          optional: mod loader patcher -> BepInEx\patchers

    Also creates <game>\Mods, where players' mods go (managed in-game from MODS on the main menu).

    The interop assemblies are generated on the player's machine from their own copy of the game (they can't be
    redistributed). They're only regenerated when GameAssembly.dll or the tools change, so re-running is quick.

.PARAMETER GameDir
    Moon Sweepers install folder. Auto-detected from Steam when omitted.

.PARAMETER PayloadDir
    Payload folder. Defaults to ..\payload relative to this script.

.PARAMETER ForceRegenerate
    Regenerate the interop assemblies even if they look up to date.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [string]$PayloadDir,
    [switch]$ForceRegenerate
)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"
. (Join-Path $PSScriptRoot "common.ps1")
if (-not $PayloadDir) { $PayloadDir = Join-Path (Split-Path -Parent $PSScriptRoot) "payload" }

function Invoke-Tool([string]$Exe, [string[]]$Arguments, [string]$LogFile) {
    # Native stderr would become terminating errors under "Stop" in Windows PowerShell; log it instead.
    $ErrorActionPreference = "Continue"
    "`r`n> $Exe $($Arguments -join ' ')" | Out-File -Append -Encoding utf8 $LogFile
    & $Exe @Arguments 2>&1 | ForEach-Object { "$_" } | Out-File -Append -Encoding utf8 $LogFile
    if ($LASTEXITCODE -ne 0) {
        Write-Host "---- last lines of $LogFile ----" -ForegroundColor Yellow
        Get-Content $LogFile -Tail 25 | Out-Host
        throw "$(Split-Path -Leaf $Exe) failed (exit code $LASTEXITCODE). Full log: $LogFile"
    }
}

function Expand-ZipOverwrite([string]$Zip, [string]$Destination) {
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $root = [IO.Path]::GetFullPath($Destination).TrimEnd("\") + "\"
    $archive = [IO.Compression.ZipFile]::OpenRead($Zip)
    try {
        foreach ($entry in $archive.Entries) {
            $target = [IO.Path]::GetFullPath((Join-Path $root $entry.FullName))
            if (-not $target.StartsWith($root, [StringComparison]::OrdinalIgnoreCase)) { throw "Unsafe path in zip: $($entry.FullName)" }
            if ($entry.FullName.EndsWith("/")) { New-Item -ItemType Directory -Force $target | Out-Null; continue }
            New-Item -ItemType Directory -Force (Split-Path -Parent $target) | Out-Null
            [IO.Compression.ZipFileExtensions]::ExtractToFile($entry, $target, $true)
        }
    } finally { $archive.Dispose() }
}

function Set-CfgValue([string]$Text, [string]$Section, [string]$Key, [string]$Value) {
    # Sets Key = Value inside [Section] of an INI-style BepInEx config, adding the key or section if missing.
    $lines = New-Object System.Collections.Generic.List[string]
    if ($Text) { $lines.AddRange([string[]]($Text -split "`r?`n")) }
    $start = -1
    for ($i = 0; $i -lt $lines.Count; $i++) { if ($lines[$i].Trim() -eq "[$Section]") { $start = $i; break } }
    if ($start -lt 0) {
        $lines.Add(""); $lines.Add("[$Section]"); $lines.Add(""); $lines.Add("$Key = $Value")
        return ($lines -join "`r`n")
    }
    for ($i = $start + 1; $i -lt $lines.Count -and $lines[$i] -notmatch '^\['; $i++) {
        if ($lines[$i] -match "^$([regex]::Escape($Key))\s*=") { $lines[$i] = "$Key = $Value"; return ($lines -join "`r`n") }
    }
    $lines.Insert($start + 1, "$Key = $Value")
    return ($lines -join "`r`n")
}

function Set-BepInExConfig([string]$BepInEx) {
    $cfgDir = Join-Path $BepInEx "config"
    $cfg = Join-Path $cfgDir "BepInEx.cfg"
    New-Item -ItemType Directory -Force $cfgDir | Out-Null
    $text = if (Test-Path $cfg) { Get-Content $cfg -Raw } else { "" }
    # BepInEx's own interop generator can't read this game's metadata; we generate the assemblies ourselves.
    $text = Set-CfgValue $text "IL2CPP" "UpdateInteropAssemblies" "false"
    # Full logging: every level, plus the game's own Unity log, so mod problems show up in LogOutput.log.
    $text = Set-CfgValue $text "Logging" "UnityLogListening" "true"
    $text = Set-CfgValue $text "Logging.Disk" "LogLevels" "All"
    $text = Set-CfgValue $text "Logging.Disk" "WriteUnityLog" "true"
    $text = Set-CfgValue $text "Logging.Console" "LogLevels" "All"
    [IO.File]::WriteAllText($cfg, $text)
}

function Install-UnityLibs([string]$GameDir, [string]$UnityLibs) {
    if (Test-Path (Join-Path $UnityLibs "UnityEngine.CoreModule.dll")) { return }
    $ggm = [Text.Encoding]::ASCII.GetString([IO.File]::ReadAllBytes((Join-Path $GameDir "MoonSweepers_Data\globalgamemanagers")), 0, 256)
    $short = [regex]::Match($ggm, '(\d{4}\.\d+\.\d+)[abfp]\d+').Groups[1].Value
    if (-not $short) { throw "Could not detect the game's Unity version." }
    Step "Downloading Unity $short base libraries"
    New-Item -ItemType Directory -Force $UnityLibs | Out-Null
    $zip = Join-Path $UnityLibs "$short.zip"
    Invoke-WebRequest "https://unity.bepinex.dev/libraries/$short.zip" -OutFile $zip -UseBasicParsing
    Expand-ZipOverwrite $zip $UnityLibs
}

# ---------------------------------------------------------------------------------------------
try {
    $pack = Get-Content (Join-Path $PayloadDir "pack.json") -Raw | ConvertFrom-Json
    Write-Host "$($pack.name) $($pack.version)" -ForegroundColor Green

    $GameDir = Resolve-GameDir $GameDir
    Write-Host "Game folder: $GameDir"
    Assert-GameClosed

    # Files extracted from a downloaded zip carry the "downloaded from the internet" mark; clear it.
    Get-ChildItem $PayloadDir -Recurse -File | Unblock-File

    $BepInEx = Join-Path $GameDir "BepInEx"
    $stampPath = Join-Path $BepInEx $StampFileName
    $stamp = $null
    if (Test-Path $stampPath) { $stamp = Get-Content $stampPath -Raw | ConvertFrom-Json }

    # 1. BepInEx + patched Il2CppInterop runtime --------------------------------------------------
    Step "Installing BepInEx ($($pack.bepinex))"
    Expand-ZipOverwrite (Join-Path $PayloadDir "BepInEx.zip") $GameDir
    Step "Installing patched Il2CppInterop runtime"
    Copy-Item -Force (Join-Path $PayloadDir "core\*") (Join-Path $BepInEx "core")
    Set-BepInExConfig $BepInEx

    # 2. Interop assemblies -----------------------------------------------------------------------
    $unityLibs = Join-Path $BepInEx "unity-libs"
    Install-UnityLibs $GameDir $unityLibs

    $interopDir = Join-Path $BepInEx "interop"
    $gameHash = (Get-FileHash (Join-Path $GameDir "GameAssembly.dll") -Algorithm SHA256).Hash
    $upToDate = $stamp -and $stamp.gameAssemblySha256 -eq $gameHash -and $stamp.toolsId -eq $pack.toolsId `
        -and (Test-Path (Join-Path $interopDir "Assembly-CSharp.dll"))

    if ($upToDate -and -not $ForceRegenerate) {
        Step "Interop assemblies are up to date"
    } else {
        Step "Generating interop assemblies from your copy of the game (under a minute)"
        $log = Join-Path $BepInEx "MoonSweepMods-install.log"
        Set-Content $log "Install log $(Get-Date -Format s)" -Encoding utf8
        $work = Join-Path ([IO.Path]::GetTempPath()) ("MoonSweepMods-" + [guid]::NewGuid().ToString("N"))
        New-Item -ItemType Directory -Force $work | Out-Null
        Push-Location $work
        try {
            $dummy = Join-Path $work "dummy"
            $generated = Join-Path $work "interop"
            Invoke-Tool (Join-Path $PayloadDir "tools\Cpp2IL.exe") @("--game-path", $GameDir, "--exe-name", "MoonSweepers",
                "--use-processor", "attributeinjector", "--output-as", "dll_default", "--output-to", $dummy) $log
            Invoke-Tool (Join-Path $PayloadDir "tools\Il2CppInterop.CLI.exe") @("generate", "--input", $dummy, "--output", $generated,
                "--unity", $unityLibs, "--game-assembly", (Join-Path $GameDir "GameAssembly.dll")) $log

            New-Item -ItemType Directory -Force $interopDir | Out-Null
            Remove-Item -Force (Join-Path $interopDir "*") -ErrorAction SilentlyContinue
            Copy-Item -Force (Join-Path $generated "*") $interopDir
        } finally {
            Pop-Location
            Remove-Item -Recurse -Force $work -ErrorAction SilentlyContinue
        }
        Write-Host ("    {0} interop assemblies written to {1}" -f (Get-ChildItem $interopDir -Filter *.dll).Count, $interopDir)
    }

    # 3. Mod loader -------------------------------------------------------------------------------
    # payload\plugins and payload\patchers hold the mod loader. Folders installed by a previous pack are replaced, so
    # renamed or removed components don't linger. Players' own mods live in <game>\Mods and are never touched.
    function Install-Components([string]$Kind, $Previous) {
        $source = Join-Path $PayloadDir $Kind
        if (-not (Test-Path $source)) { return @($Previous) }
        $targetDir = Join-Path $BepInEx $Kind
        New-Item -ItemType Directory -Force $targetDir | Out-Null
        foreach ($old in @($Previous)) {
            $path = Join-Path $targetDir $old
            if ($old -and (Test-Path $path)) { Remove-Item -Recurse -Force $path }
        }
        $installed = @()
        foreach ($item in Get-ChildItem $source -Directory) {
            Step "Installing $($item.Name)"
            Copy-Item -Recurse -Force $item.FullName $targetDir
            $installed += $item.Name
        }
        return $installed
    }
    $installedPlugins = Install-Components "plugins" $(if ($stamp) { $stamp.plugins } else { @() })
    $installedPatchers = Install-Components "patchers" $(if ($stamp) { $stamp.patchers } else { @() })
    $modsDir = Join-Path $GameDir "Mods"
    New-Item -ItemType Directory -Force $modsDir | Out-Null

    [ordered]@{
        pack               = $pack.name
        packVersion        = $pack.version
        toolsId            = $pack.toolsId
        gameAssemblySha256 = $gameHash
        plugins            = @($installedPlugins | Where-Object { $_ })
        patchers           = @($installedPatchers | Where-Object { $_ })
        installedAt        = (Get-Date -Format s)
    } | ConvertTo-Json | Set-Content $stampPath -Encoding utf8

    Write-Host ""
    Write-Host "Done! Launch Moon Sweepers from Steam as usual." -ForegroundColor Green
    Write-Host "Manage mods from MODS in the game's main menu. Mods live in: $modsDir"
    Write-Host "If the game updates, run this installer again."
    exit 0
} catch {
    Write-Host ""
    Write-Host "Install failed: $($_.Exception.Message)" -ForegroundColor Red
    if ($_.Exception.GetBaseException() -is [UnauthorizedAccessException]) {
        Write-Host "Windows blocked writing to the game folder. Try right-clicking Install.bat > Run as administrator." -ForegroundColor Yellow
    }
    exit 1
}
