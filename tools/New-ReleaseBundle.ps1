[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [string] $ArtifactsDirectory,
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$versionInfo = Get-ReleaseVersionInfo -Version $Version

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$packages = @(Get-ReleasePackageManifest -RepositoryRoot $repositoryRoot)
Assert-ReleasePackageManifestGraph -RepositoryRoot $repositoryRoot -Packages $packages
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

$nugetRoot = Join-Path $artifactsRoot 'nuget'
foreach ($package in $packages) {
    $packageId = [string]$package.id
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
$serverMetadataFiles = @(
    'appsettings.json',
    'OpenGameAgent.Server.deps.json',
    'OpenGameAgent.Server.runtimeconfig.json'
)
$serverDeps = Join-Path $serverSource 'OpenGameAgent.Server.deps.json'
$runtimeAssets = @(Resolve-PortableServerRuntimeAssets -PublishDirectory $serverSource -DepsFile $serverDeps)
foreach ($relative in $serverMetadataFiles) {
    $source = Join-Path $serverSource $relative
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Portable server file '$relative' is missing."
    }
    Copy-Item -LiteralPath $source -Destination (Join-Path $serverStage $relative)
}
foreach ($runtimeAsset in $runtimeAssets) {
    $destination = [IO.Path]::GetFullPath((Join-Path $serverStage $runtimeAsset.Destination))
    if (-not $destination.StartsWith($serverStage + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw "Portable runtime destination '$($runtimeAsset.Destination)' is unsafe."
    }
    $destinationDirectory = Split-Path -Parent $destination
    if (-not (Test-Path -LiteralPath $destinationDirectory -PathType Container)) {
        New-Item -ItemType Directory -Path $destinationDirectory -Force | Out-Null
    }
    Copy-Item -LiteralPath $runtimeAsset.Source -Destination $destination
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $serverStage 'LICENSE')
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'docs\deployment-and-security.md') -Destination (Join-Path $serverStage 'README.md')
$serverArchive = Join-Path $outputRoot "$serverBundleName.zip"
New-DirectoryArchive -Source $serverStage -Destination $serverArchive
Remove-Item -LiteralPath $serverStage -Recurse -Force
Test-PortableServerArchive -Archive $serverArchive -EntryDirectoryName $serverBundleName

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
    'Use the versioned Godot or Unity archive below for engine integration. The portable server archive runs with `dotnet OpenGameAgent.Server.dll` on a Windows or Linux .NET 8 host.',
    '',
    (Get-ReleaseStabilityNotice -VersionInfo $versionInfo)
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
