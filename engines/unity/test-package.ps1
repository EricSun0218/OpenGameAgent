[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $UnityManagedDir,
    [string] $Version = '0.3.0-alpha.1'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$engineRoot = Split-Path -Parent $PSCommandPath
$buildOutput = @(& (Join-Path $engineRoot 'build-package.ps1') -UnityManagedDir $UnityManagedDir -Version $Version)
if ($LASTEXITCODE -ne 0) { throw 'Building the Unity package failed.' }
$packagePath = [string]$buildOutput[-1]

$required = @(
    'package.json',
    'package.json.meta',
    'Runtime.meta',
    'Runtime\OpenGameAgent.Unity.asmdef',
    'Runtime\OpenGameAgent.Unity.asmdef.meta',
    'Runtime\OpenGameAgentBehaviour.cs',
    'Runtime\OpenGameAgentBehaviour.cs.meta',
    'Runtime\Plugins.meta',
    'Runtime\Plugins\OpenGameAgent.Kernel.dll',
    'Runtime\Plugins\OpenGameAgent.Kernel.dll.meta',
    'Runtime\Plugins\OpenGameAgent.dll',
    'Runtime\Plugins\OpenGameAgent.dll.meta',
    'Runtime\Plugins\OpenGameAgent.Client.dll',
    'Runtime\Plugins\OpenGameAgent.Client.dll.meta',
    'Runtime\Plugins\System.Text.Json.dll'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $packagePath $relative) -PathType Leaf)) {
        throw "Unity package is missing '$relative'."
    }
}

$unexpected = Get-ChildItem -LiteralPath $packagePath -Recurse -File |
    Where-Object { $_.Name -match '^(GameAgent\.|OpenGameAgent\.Unity\.dll)' }
if ($unexpected) {
    throw "Unity package contains an unexpected assembly: $($unexpected[0].Name)"
}

$manifest = Get-Content -LiteralPath (Join-Path $packagePath 'package.json') -Raw | ConvertFrom-Json
if ($manifest.name -ne 'com.opengameagent.runtime' -or $manifest.version -ne $Version) {
    throw 'Unity package manifest identity is invalid.'
}

$assetsWithoutMetadata = Get-ChildItem -LiteralPath $packagePath -Recurse -Force |
    Where-Object { $_.Name -notlike '*.meta' } |
    Where-Object {
        $metadataPath = $_.FullName + '.meta'
        -not (Test-Path -LiteralPath $metadataPath -PathType Leaf)
    }
if ($assetsWithoutMetadata) {
    throw "Unity package asset '$($assetsWithoutMetadata[0].FullName)' is missing stable metadata."
}

$metadataGuids = foreach ($metadata in Get-ChildItem -LiteralPath $packagePath -Recurse -File -Filter '*.meta') {
    $matches = [regex]::Matches(
        (Get-Content -LiteralPath $metadata.FullName -Raw),
        '(?m)^guid:\s*([0-9a-f]{32})\s*$')
    if ($matches.Count -ne 1) {
        throw "Unity metadata '$($metadata.FullName)' must contain exactly one lowercase 32-character GUID."
    }

    $matches[0].Groups[1].Value
}
$duplicateGuid = $metadataGuids | Group-Object | Where-Object Count -gt 1 | Select-Object -First 1
if ($duplicateGuid) {
    throw "Unity package metadata reuses GUID '$($duplicateGuid.Name)'."
}

$packagePath
