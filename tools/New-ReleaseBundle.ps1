[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [string] $ArtifactsDirectory,
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'Version must be a semantic version.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
if ([string]::IsNullOrWhiteSpace($ArtifactsDirectory)) {
    $ArtifactsDirectory = Join-Path $repositoryRoot 'artifacts'
}
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $ArtifactsDirectory 'release'
}

$artifactsRoot = [IO.Path]::GetFullPath($ArtifactsDirectory)
$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
if (-not (Test-Path -LiteralPath $artifactsRoot -PathType Container)) {
    throw "Artifacts directory '$artifactsRoot' does not exist."
}
if (-not $outputRoot.StartsWith($artifactsRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Release output must be a child of the artifacts directory.'
}
if (Test-Path -LiteralPath $outputRoot) {
    throw "Release output '$outputRoot' already exists."
}

New-Item -ItemType Directory -Path $outputRoot | Out-Null
Add-Type -AssemblyName System.IO.Compression.FileSystem

function New-DirectoryArchive {
    param(
        [Parameter(Mandatory = $true)]
        [string] $Source,
        [Parameter(Mandatory = $true)]
        [string] $Destination
    )

    if (-not (Test-Path -LiteralPath $Source -PathType Container)) {
        throw "Archive source '$Source' does not exist."
    }
    if (Test-Path -LiteralPath $Destination) {
        throw "Archive destination '$Destination' already exists."
    }

    [IO.Compression.ZipFile]::CreateFromDirectory(
        $Source,
        $Destination,
        [IO.Compression.CompressionLevel]::Optimal,
        $true)
}

$expectedPackages = @(
    'OpenGameAgent.Kernel',
    'OpenGameAgent',
    'OpenGameAgent.Persistence',
    'OpenGameAgent.Providers.OpenAICompatible',
    'OpenGameAgent.Providers.MediaHttp',
    'OpenGameAgent.Client',
    'OpenGameAgent.Extensions',
    'OpenGameAgent.Models',
    'OpenGameAgent.Connectors.Mcp'
)
$nugetRoot = Join-Path $artifactsRoot 'nuget'
foreach ($packageId in $expectedPackages) {
    $name = "$packageId.$Version.nupkg"
    $source = Join-Path $nugetRoot $name
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Release package '$name' is missing."
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $outputRoot $name)
}

$godotSource = Join-Path $artifactsRoot 'godot\open-game-agent-godot'
$unitySource = Join-Path $artifactsRoot 'unity\com.opengameagent.runtime'
New-DirectoryArchive -Source $godotSource -Destination (Join-Path $outputRoot "OpenGameAgent.Godot-$Version.zip")
New-DirectoryArchive -Source $unitySource -Destination (Join-Path $outputRoot "OpenGameAgent.Unity-$Version.zip")

$serverSource = Join-Path $artifactsRoot 'server'
$serverBundleName = "OpenGameAgent.Server-$Version-portable"
$serverStage = Join-Path $outputRoot $serverBundleName
if (-not $serverStage.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'Server staging path is unsafe.'
}
New-Item -ItemType Directory -Path $serverStage | Out-Null
$serverFiles = @(
    'appsettings.json',
    'OpenGameAgent.Kernel.dll',
    'OpenGameAgent.dll',
    'OpenGameAgent.Persistence.dll',
    'OpenGameAgent.Providers.OpenAICompatible.dll',
    'OpenGameAgent.Server.deps.json',
    'OpenGameAgent.Server.dll',
    'OpenGameAgent.Server.runtimeconfig.json'
)
foreach ($relative in $serverFiles) {
    $source = Join-Path $serverSource $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Portable server file '$relative' is missing."
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $serverStage $relative)
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $serverStage 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\deployment-and-security.md') -Destination (Join-Path $serverStage 'README.md')
New-DirectoryArchive -Source $serverStage -Destination (Join-Path $outputRoot "$serverBundleName.zip")
Remove-Item -LiteralPath $serverStage -Recurse -Force

$changelog = Get-Content -LiteralPath (Join-Path $repositoryRoot 'CHANGELOG.md') -Raw
$escapedVersion = [regex]::Escape($Version)
$match = [regex]::Match($changelog, "(?ms)^##\s+$escapedVersion\s*\r?\n(?<body>.*?)(?=^##\s+|\z)")
if (-not $match.Success) {
    throw "CHANGELOG.md does not contain version '$Version'."
}
$releaseNotes = @(
    "# OpenGameAgent v$Version",
    '',
    $match.Groups['body'].Value.Trim(),
    '',
    '## Install',
    '',
    '```bash',
    "dotnet add package OpenGameAgent --version $Version",
    '```',
    '',
    'Use the versioned Godot or Unity archive below for engine integration. The portable server archive runs with `dotnet OpenGameAgent.Server.dll` on a .NET 8 host.',
    '',
    'This is an alpha release. Public APIs can change before 1.0.'
) -join [Environment]::NewLine
$releaseNotes | Set-Content -LiteralPath (Join-Path $outputRoot 'RELEASE_NOTES.md') -Encoding utf8NoBOM

$downloadAssets = Get-ChildItem -LiteralPath $outputRoot -File |
    Where-Object Name -ne 'RELEASE_NOTES.md' |
    Sort-Object Name
$checksumLines = foreach ($asset in $downloadAssets) {
    $hash = (Get-FileHash -LiteralPath $asset.FullName -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $($asset.Name)"
}
$checksumLines | Set-Content -LiteralPath (Join-Path $outputRoot 'SHA256SUMS.txt') -Encoding utf8NoBOM

Write-Output $outputRoot
