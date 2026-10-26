<#
.SYNOPSIS
    Renders ModelKit designs (examples\*\Design\*.cs) to PNG without the game.

.EXAMPLE
    .\tools\preview-model.ps1 HexHarvester            # artifacts\previews\HexHarvester.png
    .\tools\preview-model.ps1 HexHarvester -View side # one view, full size
#>
[CmdletBinding(PositionalBinding = $false)]
param(
    [Parameter(Position = 0)][string]$Design = "all",
    [string]$View,
    [string]$Size = "1920x1080"
)

$ErrorActionPreference = "Stop"
$env:DOTNET_CLI_TELEMETRY_OPTOUT = "1"
$env:DOTNET_NOLOGO = "1"
$project = Join-Path $PSScriptRoot "ModelPreview\ModelPreview.csproj"
$runArgs = @("run", "--project", $project, "-c", "Release", "--", $Design, "--size", $Size)
if ($View) { $runArgs += @("--view", $View) }
& dotnet @runArgs
exit $LASTEXITCODE
