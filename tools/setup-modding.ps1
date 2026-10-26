<#
.SYNOPSIS
    Developer setup: installs BepInEx 6 (IL2CPP), patched for Moon Sweepers (Unity 6000.6 / IL2CPP metadata v108),
    and generates the interop assemblies that mods compile against.

.DESCRIPTION
    Stock BepInEx can't mod this game yet, for two reasons:
      1. Its bundled Cpp2IL only reads IL2CPP metadata up to v106 (the game uses v108).
      2. Il2CppInterop has no native struct definition for Unity 6000.6's Il2CppClass layout.

    This script works around both:
      - tools\build-tools.ps1 builds a current Cpp2IL and an Il2CppInterop that includes
        tools\il2cppinterop\Class_108_0.cs, and assembles them with BepInEx into .tools\payload.
      - installer\scripts\install.ps1 (the same installer players run) installs that payload into the game folder,
        generates the interop assemblies, and turns off BepInEx's own (broken for this game) interop generation.

    Re-run this after every game update so the interop assemblies match the new GameAssembly.dll.

.PARAMETER GameDir
    Moon Sweepers install folder. Auto-detected from Steam when omitted.

.PARAMETER SkipBuild
    Reuse previously built tools in .tools instead of fetching/rebuilding them.

.PARAMETER ForceRegenerate
    Regenerate the interop assemblies even if GameAssembly.dll and the tools haven't changed.
#>
[CmdletBinding()]
param(
    [string]$GameDir,
    [switch]$SkipBuild,
    [switch]$ForceRegenerate
)

$ErrorActionPreference = "Stop"
$RepoRoot = Split-Path -Parent $PSScriptRoot

$payload = & (Join-Path $PSScriptRoot "build-tools.ps1") -SkipBuild:$SkipBuild

$installArgs = @{ PayloadDir = $payload; ForceRegenerate = $ForceRegenerate }
if ($GameDir) { $installArgs.GameDir = $GameDir }
& (Join-Path $RepoRoot "installer\scripts\install.ps1") @installArgs
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Write-Host "Build mods with:  dotnet build -c Release   (from the repo root)"
