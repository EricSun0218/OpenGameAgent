[CmdletBinding()]
param(
    [string] $Version = '0.3.0-alpha.4'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$engineRoot = Split-Path -Parent $PSCommandPath
$buildOutput = @(& (Join-Path $engineRoot 'build-package.ps1') -Version $Version)
$packageRoot = [string]$buildOutput[-1]

$required = @(
    'LICENSE',
    'README.md',
    'OpenGameAgent.uplugin',
    'Source\OpenGameAgentUnreal\OpenGameAgentUnreal.Build.cs',
    'Source\OpenGameAgentUnreal\Public\OpenGameAgentSubsystem.h',
    'Source\OpenGameAgentUnreal\Private\OpenGameAgentSubsystem.cpp',
    'Source\OpenGameAgentUnreal\Private\OpenGameAgentSseParser.h',
    'Source\OpenGameAgentUnreal\Private\OpenGameAgentSseParser.cpp',
    'Source\OpenGameAgentUnreal\Private\OpenGameAgentUnrealModule.cpp',
    'Source\OpenGameAgentUnreal\Private\Tests\OpenGameAgentSseParserTests.cpp'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $relative) -PathType Leaf)) {
        throw "Unreal package is missing '$relative'."
    }
}

$repositoryRoot = Split-Path -Parent (Split-Path -Parent $engineRoot)
$sourceLicenseHash = (Get-FileHash -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Algorithm SHA256).Hash
$packageLicenseHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'LICENSE') -Algorithm SHA256).Hash
if ($sourceLicenseHash -ne $packageLicenseHash) {
    throw 'Unreal package must contain the complete repository license.'
}

$descriptor = Get-Content -LiteralPath (Join-Path $packageRoot 'OpenGameAgent.uplugin') -Raw | ConvertFrom-Json
if ($descriptor.FriendlyName -ne 'OpenGameAgent' -or
    $descriptor.VersionName -ne $Version -or
    $descriptor.Modules.Count -ne 1 -or
    $descriptor.Modules[0].Name -ne 'OpenGameAgentUnreal' -or
    $descriptor.Modules[0].Type -ne 'Runtime') {
    throw 'Unreal plugin descriptor identity is invalid.'
}

$generated = Get-ChildItem -LiteralPath $packageRoot -Recurse -Force |
    Where-Object { $_.Name -in @('Binaries', 'Intermediate', 'Saved', '.vs') }
if ($generated) {
    throw "Unreal package contains generated state '$($generated[0].FullName)'."
}

$forbiddenExtensions = @('.dll', '.exe', '.pdb', '.obj', '.lib', '.exp')
$binary = Get-ChildItem -LiteralPath $packageRoot -Recurse -File |
    Where-Object { $forbiddenExtensions -contains $_.Extension.ToLowerInvariant() } |
    Select-Object -First 1
if ($binary) {
    throw "Unreal source package contains an unexpected binary '$($binary.Name)'."
}

$packageRoot
