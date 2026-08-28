param(
    [string]$EngineRoot = $env:UE_ROOT,
    [string]$Output = "$PSScriptRoot/artifacts/OpenGameAgent"
)

$ErrorActionPreference = "Stop"
if ([string]::IsNullOrWhiteSpace($EngineRoot)) { throw "Set UE_ROOT or pass -EngineRoot." }
$uat = Join-Path $EngineRoot "Engine/Build/BatchFiles/RunUAT.bat"
$plugin = (Resolve-Path "$PSScriptRoot/Plugins/OpenGameAgent/OpenGameAgent.uplugin").Path
$outputRoot = [IO.Path]::GetFullPath($Output)
if (-not (Test-Path -LiteralPath $uat)) { throw "Unreal Automation Tool was not found." }
if (-not $outputRoot.StartsWith([IO.Path]::GetFullPath($PSScriptRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Plugin output must remain inside the Unreal adapter directory."
}
& $uat BuildPlugin "-Plugin=$plugin" "-Package=$outputRoot" -TargetPlatforms=Win64 -StrictIncludes
exit $LASTEXITCODE
