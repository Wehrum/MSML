<#
.SYNOPSIS
    Builds everything the installer needs except the mods, and writes it to .tools\payload.

.DESCRIPTION
    Produces the payload consumed by installer\scripts\install.ps1:
      - BepInEx.zip                 pinned BepInEx Bleeding Edge build (IL2CPP x64)
      - core\*.dll                  Il2CppInterop runtime built with tools\il2cppinterop\Class_108_0.cs
      - tools\Cpp2IL.exe            self-contained Cpp2IL (reads IL2CPP metadata v108)
      - tools\Il2CppInterop.CLI.exe self-contained patched interop generator
      - pack.json                   metadata (versions, toolsId)

    Sources are fetched and built under .tools (git-ignored). The .NET SDKs needed are installed privately into
    .tools if they're not already on the machine.

.PARAMETER SkipBuild
    Reuse previously built tools when they exist.

.OUTPUTS
    The payload folder path.
#>
[CmdletBinding()]
param([switch]$SkipBuild)

$ErrorActionPreference = "Stop"
$ProgressPreference = "SilentlyContinue"

# Pinned versions known to work with Moon Sweepers (Unity 6000.6.0f1).
$BepInExBuild        = "6.0.0-be.791+b782da4"
$BepInExUrl          = "https://builds.bepinex.dev/projects/bepinex_be/791/BepInEx-Unity.IL2CPP-win-x64-6.0.0-be.791%2Bb782da4.zip"
$Cpp2ILRepo          = "https://github.com/SamboyCoding/Cpp2IL.git"
$Cpp2ILCommit        = "b5ad444b82267cb1e4b88b8b373c008105bdea52"
$Il2CppInteropRepo   = "https://github.com/BepInEx/Il2CppInterop.git"
$Il2CppInteropCommit = "81a6f78"

$RepoRoot = Split-Path -Parent $PSScriptRoot
$Tools    = Join-Path $RepoRoot ".tools"
$Payload  = Join-Path $Tools "payload"
$ClassPatch = Join-Path $PSScriptRoot "il2cppinterop\Class_108_0.cs"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"

function Step($msg) { Write-Host "==> $msg" -ForegroundColor Cyan }

function Invoke-Checked {
    param([string]$Exe, [string[]]$Arguments)
    & $Exe @Arguments | Out-Host
    if ($LASTEXITCODE -ne 0) { throw "Command failed ($LASTEXITCODE): $Exe $($Arguments -join ' ')" }
}

function Get-DotnetWithSdk([string]$major) {
    # Prefer a system SDK; otherwise install one privately under .tools.
    $sys = Get-Command dotnet -ErrorAction SilentlyContinue
    if ($sys -and ((& $sys.Source --list-sdks) -match "^$major\.")) { return $sys.Source }

    $dir = Join-Path $Tools "dotnet$major"
    $exe = Join-Path $dir "dotnet.exe"
    if ((Test-Path $exe) -and ((& $exe --list-sdks) -match "^$major\.")) { return $exe }

    Step "Installing .NET $major SDK locally into $dir"
    $installer = Join-Path $Tools "dotnet-install.ps1"
    if (-not (Test-Path $installer)) { Invoke-WebRequest https://dot.net/v1/dotnet-install.ps1 -OutFile $installer -UseBasicParsing }
    # Execution policy bypass applies only to this child process.
    Invoke-Checked powershell @("-NoProfile", "-ExecutionPolicy", "Bypass", "-File", $installer,
        "-Channel", "$major.0", "-InstallDir", $dir, "-NoPath")
    return $exe
}

function Sync-GitRepo([string]$url, [string]$dir, [string]$commit) {
    if (-not (Test-Path (Join-Path $dir ".git"))) { Invoke-Checked git @("clone", "-q", $url, $dir) }
    Invoke-Checked git @("-C", $dir, "fetch", "-q", "origin")
    Invoke-Checked git @("-C", $dir, "checkout", "-q", "-f", $commit)
}

# ---------------------------------------------------------------------------------------------
New-Item -ItemType Directory -Force $Tools | Out-Null
# Stop third-party builds under .tools from inheriting the repo's mod Directory.Build.props.
Set-Content (Join-Path $Tools "Directory.Build.props") "<Project />"
Set-Content (Join-Path $Tools "Directory.Build.targets") "<Project />"

$cpp2ilSrc  = Join-Path $Tools "src\Cpp2IL"
$interopSrc = Join-Path $Tools "src\Il2CppInterop"
$cpp2ilPub  = Join-Path $Tools "publish\Cpp2IL"
$interopPub = Join-Path $Tools "publish\Il2CppInterop.CLI"
$interopBin = Join-Path $Tools "bin\Il2CppInterop"
$coreFiles  = "Il2CppInterop.Runtime.dll", "Il2CppInterop.Common.dll", "Il2CppInterop.Generator.dll",
              "Il2CppInterop.HarmonySupport.dll", "TerraFX.Interop.Windows.dll"   # TerraFX: safe memory scanning on Unity 6

$publishArgs = @("-c", "Release", "-r", "win-x64", "--self-contained", "-p:PublishSingleFile=true",
    "-p:IncludeNativeLibrariesForSelfExtract=true", "-p:EnableCompressionInSingleFile=true",
    "-p:GenerateDocumentationFile=false", "-v", "q", "-nologo")

if (-not $SkipBuild -or -not (Test-Path (Join-Path $cpp2ilPub "Cpp2IL.exe"))) {
    $dotnet10 = Get-DotnetWithSdk "10"   # Cpp2IL uses C# 14
    Step "Building Cpp2IL ($Cpp2ILCommit)"
    Sync-GitRepo $Cpp2ILRepo $cpp2ilSrc $Cpp2ILCommit
    Remove-Item -Recurse -Force $cpp2ilPub -ErrorAction SilentlyContinue
    Invoke-Checked $dotnet10 (@("publish", (Join-Path $cpp2ilSrc "Cpp2IL\Cpp2IL.csproj"), "-f", "net10.0", "-o", $cpp2ilPub) + $publishArgs)
}

$interopReady = (Test-Path (Join-Path $interopPub "Il2CppInterop.CLI.exe")) -and
    -not ($coreFiles | Where-Object { -not (Test-Path (Join-Path $interopBin $_)) })
if (-not $SkipBuild -or -not $interopReady) {
    $dotnet8 = Get-DotnetWithSdk "8"     # Il2CppInterop's global.json pins the 8.0 SDK
    Step "Building Il2CppInterop ($Il2CppInteropCommit) with the Unity 6000.6 class handler"
    Sync-GitRepo $Il2CppInteropRepo $interopSrc $Il2CppInteropCommit
    Copy-Item -Force $ClassPatch (Join-Path $interopSrc "Il2CppInterop.Runtime\Runtime\VersionSpecific\Class\")
    Push-Location $interopSrc
    try {
        # Runtime libraries that replace BepInEx's copies in BepInEx\core.
        Invoke-Checked $dotnet8 @("build", "Il2CppInterop.HarmonySupport\Il2CppInterop.HarmonySupport.csproj", "-c", "Release", "-o", $interopBin,
            "-v", "q", "-nologo", "-p:CopyLocalLockFileAssemblies=true")
        Invoke-Checked $dotnet8 @("build", "Il2CppInterop.CLI\Il2CppInterop.CLI.csproj", "-c", "Release", "-o", $interopBin, "-v", "q", "-nologo")
        # Standalone generator that runs on the player's machine without a .NET install.
        # Trim unused framework code from it (~57 MB -> ~34 MB). This has to be set in the CLI project: a command-line
        # property would also apply to its netstandard dependencies, which can't be trimmed.
        $cliProj = Join-Path $interopSrc "Il2CppInterop.CLI\Il2CppInterop.CLI.csproj"
        $cliXml = [IO.File]::ReadAllText($cliProj)
        if ($cliXml -notmatch "<PublishTrimmed>") {
            $cliXml = $cliXml.Replace("</Project>", "  <PropertyGroup><PublishTrimmed>true</PublishTrimmed><TrimMode>partial</TrimMode></PropertyGroup>`r`n</Project>")
            [IO.File]::WriteAllText($cliProj, $cliXml)
        }
        Remove-Item -Recurse -Force $interopPub -ErrorAction SilentlyContinue
        Invoke-Checked $dotnet8 (@("publish", "Il2CppInterop.CLI\Il2CppInterop.CLI.csproj", "-o", $interopPub, "-p:PackAsTool=false") + $publishArgs)
    } finally { Pop-Location }
}

# ---------------------------------------------------------------------------------------------
Step "Assembling payload in $Payload"
Remove-Item -Recurse -Force $Payload -ErrorAction SilentlyContinue
New-Item -ItemType Directory -Force (Join-Path $Payload "core"), (Join-Path $Payload "tools") | Out-Null

$bepinexZip = Join-Path $Tools "BepInEx-$($BepInExBuild -replace '\+', '_').zip"
if (-not (Test-Path $bepinexZip)) {
    Step "Downloading BepInEx $BepInExBuild"
    Invoke-WebRequest $BepInExUrl -OutFile "$bepinexZip.tmp" -UseBasicParsing
    Move-Item -Force "$bepinexZip.tmp" $bepinexZip
}
Copy-Item $bepinexZip (Join-Path $Payload "BepInEx.zip")
foreach ($f in $coreFiles) { Copy-Item (Join-Path $interopBin $f) (Join-Path $Payload "core") }
Copy-Item (Join-Path $cpp2ilPub "Cpp2IL.exe") (Join-Path $Payload "tools")
Copy-Item (Join-Path $interopPub "Il2CppInterop.CLI.exe") (Join-Path $Payload "tools")

# Changing any of these invalidates previously generated interop assemblies.
$patchHash = (Get-FileHash $ClassPatch -Algorithm SHA256).Hash.Substring(0, 12)
[ordered]@{
    name                = "Moon Sweepers mod pack"
    version             = "dev"
    bepinex             = $BepInExBuild
    bepinexUrl          = $BepInExUrl
    cpp2ilCommit        = $Cpp2ILCommit
    il2cppInteropCommit = $Il2CppInteropCommit
    toolsId             = "cpp2il-$($Cpp2ILCommit.Substring(0, 7))_interop-$Il2CppInteropCommit-$patchHash"
    plugins             = @()
} | ConvertTo-Json | Set-Content (Join-Path $Payload "pack.json") -Encoding utf8

$Payload
