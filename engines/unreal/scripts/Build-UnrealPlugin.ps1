[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EngineRoot,

    [string[]]$TargetPlatforms = @('Win64'),

    [string]$PackageDirectory
)

$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Resolve-Path (Join-Path $pluginRoot '..\..')
$pluginFile = Join-Path $pluginRoot 'GameAgentRuntime.uplugin'

if ([string]::IsNullOrWhiteSpace($PackageDirectory)) {
    $PackageDirectory = Join-Path $repositoryRoot 'artifacts\unreal-plugin-package'
}

$resolvedEngineRoot = Resolve-Path -LiteralPath $EngineRoot
$runUat = Join-Path $resolvedEngineRoot 'Engine\Build\BatchFiles\RunUAT.bat'
if (-not (Test-Path -LiteralPath $runUat -PathType Leaf)) {
    throw "RunUAT.bat was not found below the supplied EngineRoot."
}
if (-not (Test-Path -LiteralPath $pluginFile -PathType Leaf)) {
    throw 'GameAgentRuntime.uplugin was not found.'
}
if ($TargetPlatforms.Count -eq 0) {
    throw 'At least one target platform is required.'
}

$platformArgument = $TargetPlatforms -join '+'
& $runUat `
    BuildPlugin `
    "-Plugin=$pluginFile" `
    "-Package=$PackageDirectory" `
    "-TargetPlatforms=$platformArgument" `
    -Rocket

if ($LASTEXITCODE -ne 0) {
    throw "Unreal plugin build failed with exit code $LASTEXITCODE."
}

Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') `
    -Destination (Join-Path $PackageDirectory 'LICENSE') -Force
$packagedLicense = Join-Path $PackageDirectory 'LICENSE'
if (-not (Test-Path -LiteralPath $packagedLicense -PathType Leaf)) {
    throw 'Packaged Unreal plugin is missing LICENSE.'
}
$licenseText = Get-Content -LiteralPath $packagedLicense -Raw
if ($licenseText -notmatch 'Apache License' `
    -or $licenseText -notmatch 'TERMS AND CONDITIONS') {
    throw 'Packaged Unreal plugin does not contain the complete license.'
}

& (Join-Path $repositoryRoot `
    'engines\shared\Test-ReleaseArtifactPrivacy.ps1') `
    -Path $PackageDirectory
if ($LASTEXITCODE -ne 0) {
    throw 'Unreal plugin privacy scan failed.'
}
