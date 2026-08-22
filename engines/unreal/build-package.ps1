[CmdletBinding()]
param(
    [string] $Version = '0.3.0-alpha.4',
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'Version must be a semantic version.'
}

$engineRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $engineRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\unreal'
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$packageRoot = Join-Path $outputRoot 'OpenGameAgent'
if ($packageRoot -eq $outputRoot -or
    -not $packageRoot.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Unreal package output path is unsafe.'
}

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}
New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $engineRoot 'Plugins\OpenGameAgent') -Destination $packageRoot -Recurse
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $packageRoot 'LICENSE')
Copy-Item -LiteralPath (Join-Path $engineRoot 'README.md') -Destination (Join-Path $packageRoot 'README.md')

$descriptorPath = Join-Path $packageRoot 'OpenGameAgent.uplugin'
$descriptor = Get-Content -LiteralPath $descriptorPath -Raw | ConvertFrom-Json
$descriptor.VersionName = $Version
$descriptor | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $descriptorPath -Encoding utf8NoBOM

$packageRoot
