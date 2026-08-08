[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $PackageVersion,
    [string] $PackagesDirectory = 'artifacts/nuget',
    [string] $ExpectedRepositoryCommit,
    [switch] $SkipConsumerRestore
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-ReleaseVersionInfo -Version $PackageVersion
$packages = @(Get-ReleasePackageManifest -RepositoryRoot $repositoryRoot)
Assert-ReleasePackageManifestGraph -RepositoryRoot $repositoryRoot -Packages $packages

$packageRoot = if ([IO.Path]::IsPathRooted($PackagesDirectory)) {
    [IO.Path]::GetFullPath($PackagesDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackagesDirectory))
}
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "NuGet package directory '$packageRoot' does not exist."
}

$expectedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($package in $packages) {
    $null = $expectedFiles.Add("$($package.id).$PackageVersion.nupkg")
}
$actualFiles = @(Get-ChildItem -LiteralPath $packageRoot -Filter "*.$PackageVersion.nupkg" -File)
if ($actualFiles.Count -ne $expectedFiles.Count) {
    throw "Expected $($expectedFiles.Count) versioned packages but found $($actualFiles.Count)."
}
foreach ($actualFile in $actualFiles) {
    if (-not $expectedFiles.Contains($actualFile.Name)) {
        throw "Unexpected release package '$($actualFile.Name)'."
    }
}
foreach ($expectedFile in $expectedFiles) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $expectedFile) -PathType Leaf)) {
        throw "Expected release package '$expectedFile' is missing."
    }
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$idByProject = @{}
foreach ($package in $packages) {
    $idByProject[[IO.Path]::GetFullPath([string]$package.FullProjectPath)] = [string]$package.id
}
foreach ($package in $packages) {
    $expectedDependencies = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    [xml]$projectXml = Get-Content -LiteralPath $package.FullProjectPath
    $targetFrameworkNodes = @($projectXml.SelectNodes('/Project/PropertyGroup/TargetFramework'))
    if ($targetFrameworkNodes.Count -ne 1 -or [string]::IsNullOrWhiteSpace($targetFrameworkNodes[0].InnerText)) {
        throw "Release project '$($package.id)' must declare exactly one target framework."
    }
    $targetFramework = [string]$targetFrameworkNodes[0].InnerText
    $assemblyNameNodes = @($projectXml.SelectNodes('/Project/PropertyGroup/AssemblyName'))
    $assemblyName = if ($assemblyNameNodes.Count -gt 0) {
        [string]$assemblyNameNodes[-1].InnerText
    }
    else {
        [IO.Path]::GetFileNameWithoutExtension([string]$package.FullProjectPath)
    }
    foreach ($reference in @($projectXml.SelectNodes('/Project/ItemGroup/ProjectReference'))) {
        $portableReference = ([string]$reference.Include).Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar)
        $dependencyProject = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $package.FullProjectPath) $portableReference))
        if ($idByProject.ContainsKey($dependencyProject)) {
            $expectedDependencies.Add($idByProject[$dependencyProject], "[$PackageVersion]")
        }
    }
    foreach ($reference in @($projectXml.SelectNodes('/Project/ItemGroup/PackageReference'))) {
        $dependencyId = [string]$reference.Include
        $dependencyVersion = [string]$reference.Version
        if ([string]::IsNullOrWhiteSpace($dependencyId) -or [string]::IsNullOrWhiteSpace($dependencyVersion)) {
            throw "Release project '$($package.id)' contains an unresolved package dependency."
        }
        if ($expectedDependencies.ContainsKey($dependencyId)) {
            throw "Release project '$($package.id)' contains duplicate dependency '$dependencyId'."
        }
        $expectedDependencies.Add($dependencyId, $dependencyVersion)
    }

    $packagePath = Join-Path $packageRoot "$($package.id).$PackageVersion.nupkg"
    $archive = [IO.Compression.ZipFile]::OpenRead($packagePath)
    try {
        $nuspecEntries = @($archive.Entries | Where-Object FullName -like '*.nuspec')
        if ($nuspecEntries.Count -ne 1) {
            throw "Release package '$($package.id)' contains $($nuspecEntries.Count) nuspec manifests."
        }
        $expectedAssemblyPath = "lib/$targetFramework/$assemblyName.dll"
        $compileAssets = @($archive.Entries | Where-Object {
            $_.FullName -ieq $expectedAssemblyPath
        })
        if ($compileAssets.Count -ne 1 -or $compileAssets[0].Length -le 0) {
            throw "Release package '$($package.id)' does not contain non-empty '$expectedAssemblyPath'."
        }
        $reader = [IO.StreamReader]::new($nuspecEntries[0].Open())
        try {
            [xml]$nuspec = $reader.ReadToEnd()
        }
        finally {
            $reader.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    $metadataNode = $nuspec.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
    $idNode = $metadataNode.SelectSingleNode('./*[local-name()="id"]')
    $versionNode = $metadataNode.SelectSingleNode('./*[local-name()="version"]')
    if ($null -eq $idNode -or -not [string]::Equals($idNode.InnerText, [string]$package.id, [StringComparison]::Ordinal)) {
        throw "Release package '$($package.id)' has an unexpected package id."
    }
    if ($null -eq $versionNode -or -not [string]::Equals($versionNode.InnerText, $PackageVersion, [StringComparison]::Ordinal)) {
        throw "Release package '$($package.id)' has an unexpected package version."
    }
    if (-not [string]::IsNullOrWhiteSpace($ExpectedRepositoryCommit)) {
        if ($ExpectedRepositoryCommit -notmatch '^[0-9a-fA-F]{40}$') {
            throw 'Expected repository commit must be a full SHA-1 object id.'
        }
        $repositoryNode = $metadataNode.SelectSingleNode('./*[local-name()="repository"]')
        if ($null -eq $repositoryNode -or
            -not [string]::Equals([string]$repositoryNode.commit, $ExpectedRepositoryCommit, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release package '$($package.id)' is not bound to repository commit '$ExpectedRepositoryCommit'."
        }
    }

    $actualDependencies = @{}
    foreach ($dependencyNode in @($metadataNode.SelectNodes('.//*[local-name()="dependency"]'))) {
        $dependencyId = [string]$dependencyNode.id
        $dependencyVersion = [string]$dependencyNode.version
        if ($actualDependencies.ContainsKey($dependencyId) -and
            -not [string]::Equals($actualDependencies[$dependencyId], $dependencyVersion, [StringComparison]::Ordinal)) {
            throw "Release package '$($package.id)' contains conflicting versions for '$dependencyId'."
        }
        $actualDependencies[$dependencyId] = $dependencyVersion
    }
    if ($actualDependencies.Count -ne $expectedDependencies.Count) {
        throw "Release package '$($package.id)' dependency count does not match its project references and package references."
    }
    foreach ($expectedDependency in $expectedDependencies.Keys) {
        if (-not $actualDependencies.ContainsKey($expectedDependency)) {
            throw "Release package '$($package.id)' omits dependency '$expectedDependency'."
        }
        if (-not [string]::Equals(
            $actualDependencies[$expectedDependency],
            $expectedDependencies[$expectedDependency],
            [StringComparison]::Ordinal)) {
            throw "Release package '$($package.id)' has an unexpected version for dependency '$expectedDependency'."
        }
    }
}

if ($SkipConsumerRestore) {
    Write-Output "Statically verified all $($packages.Count) release packages."
    return
}

$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('opengameagent-nuget-smoke-' + [Guid]::NewGuid().ToString('N'))
try {
    $consumerRoot = Join-Path $temporaryRoot 'consumer'
    $packageCache = Join-Path $temporaryRoot 'packages'
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null

    & dotnet new classlib --framework netstandard2.1 --name ReleaseConsumer --output $consumerRoot --no-restore
    if ($LASTEXITCODE -ne 0) {
        throw 'Creating the clean NuGet consumer failed.'
    }
    $consumerProject = Join-Path $consumerRoot 'ReleaseConsumer.csproj'
    foreach ($package in $packages) {
        & dotnet add $consumerProject package $package.id --version $PackageVersion --source $packageRoot --no-restore
        if ($LASTEXITCODE -ne 0) {
            throw "Adding release package '$($package.id)' to the clean consumer failed."
        }
    }

    $escapedPackageRoot = [Security.SecurityElement]::Escape($packageRoot)
    $nugetConfig = Join-Path $temporaryRoot 'NuGet.Config'
    $configuration = @"
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <clear />
    <add key="release" value="$escapedPackageRoot" />
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" protocolVersion="3" />
  </packageSources>
</configuration>
"@
    [IO.File]::WriteAllText($nugetConfig, $configuration, [Text.UTF8Encoding]::new($false))

    & dotnet restore $consumerProject --configfile $nugetConfig --packages $packageCache --no-cache --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Restoring the clean NuGet consumer failed.'
    }
    & dotnet build $consumerProject -c Release --no-restore --nologo
    if ($LASTEXITCODE -ne 0) {
        throw 'Building the clean NuGet consumer failed.'
    }

    $assetsPath = Join-Path $consumerRoot 'obj/project.assets.json'
    $assets = Get-Content -LiteralPath $assetsPath -Raw | ConvertFrom-Json
    $libraries = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($libraryName in $assets.libraries.PSObject.Properties.Name) {
        $null = $libraries.Add([string]$libraryName)
    }

    foreach ($package in $packages) {
        $identity = "$($package.id)/$PackageVersion"
        if (-not $libraries.Contains($identity)) {
            throw "Clean consumer assets do not contain '$identity'."
        }

        $metadataPath = Join-Path $packageCache (Join-Path $package.id.ToLowerInvariant() (Join-Path $versionInfo.FlatContainerVersion '.nupkg.metadata'))
        if (-not (Test-Path -LiteralPath $metadataPath -PathType Leaf)) {
            throw "Restore metadata for '$identity' is missing."
        }
        $metadata = Get-Content -LiteralPath $metadataPath -Raw | ConvertFrom-Json
        $metadataSource = [IO.Path]::GetFullPath([string]$metadata.source)
        if (-not [string]::Equals($metadataSource.TrimEnd([IO.Path]::DirectorySeparatorChar), $packageRoot.TrimEnd([IO.Path]::DirectorySeparatorChar), [StringComparison]::OrdinalIgnoreCase)) {
            throw "Clean consumer restored '$identity' from '$metadataSource' instead of the release package directory."
        }

        $packagePath = Join-Path $packageRoot "$($package.id).$PackageVersion.nupkg"
        $stream = [IO.File]::OpenRead($packagePath)
        try {
            $sha512 = [Security.Cryptography.SHA512]::Create()
            try {
                $contentHash = [Convert]::ToBase64String($sha512.ComputeHash($stream))
            }
            finally {
                $sha512.Dispose()
            }
        }
        finally {
            $stream.Dispose()
        }
        if (-not [string]::Equals($contentHash, [string]$metadata.contentHash, [StringComparison]::Ordinal)) {
            throw "Clean consumer content hash for '$identity' does not match the release package."
        }
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output "Clean consumer restored and built all $($packages.Count) release packages."
