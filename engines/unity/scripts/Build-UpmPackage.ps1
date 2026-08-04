[CmdletBinding()]
param(
    [ValidateSet("Debug", "Release")]
    [string]$Configuration = "Release",
    [switch]$IncludeSymbols,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$unityRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $unityRoot "..\.."))
$templatePath = Join-Path $unityRoot "com.gameagent.runtime.unity"
$artifactRoot = [IO.Path]::GetFullPath((Join-Path $unityRoot "artifacts"))
$publishedOutputPath = [IO.Path]::GetFullPath(
    (Join-Path $artifactRoot "com.gameagent.runtime.unity"))
$stagingName = ".com.gameagent.runtime.unity." `
    + [Guid]::NewGuid().ToString("N") `
    + ".staging"
$outputPath = [IO.Path]::GetFullPath(
    (Join-Path $artifactRoot $stagingName))
$smokeProject = Join-Path $repositoryRoot `
    "tests\UnityCompileSmoke\UnityCompileSmoke.csproj"
$smokeOutput = Join-Path $repositoryRoot `
    "tests\UnityCompileSmoke\bin\$Configuration\netstandard2.1"
if (Test-Path -LiteralPath $publishedOutputPath) {
    if (-not $Force) {
        throw "Artifact already exists at '$publishedOutputPath'. Pass -Force to rebuild."
    }

    $requiredPrefix = $artifactRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar) `
        + [IO.Path]::DirectorySeparatorChar
    if (-not $publishedOutputPath.StartsWith(
            $requiredPrefix,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "Refusing to replace an output outside '$artifactRoot'."
    }
}

function Publish-DirectoryAtomically {
    param(
        [Parameter(Mandatory = $true)][string]$StagedPath,
        [Parameter(Mandatory = $true)][string]$Destination
    )

    $resolvedStaged = [IO.Path]::GetFullPath($StagedPath)
    $resolvedDestination = [IO.Path]::GetFullPath($Destination)
    if (-not (Test-Path -LiteralPath $resolvedStaged -PathType Container)) {
        throw "The staged Unity package does not exist."
    }
    if (-not [string]::Equals(
            [IO.Path]::GetDirectoryName($resolvedStaged),
            [IO.Path]::GetDirectoryName($resolvedDestination),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The staged and published Unity packages must share a directory."
    }

    if (-not (Test-Path -LiteralPath $resolvedDestination)) {
        [IO.Directory]::Move($resolvedStaged, $resolvedDestination)
        return
    }

    $destinationItem = Get-Item -LiteralPath $resolvedDestination
    if (-not $destinationItem.PSIsContainer `
        -or ($destinationItem.Attributes `
            -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The published Unity package must be a regular directory."
    }

    $backup = [IO.Path]::GetFullPath(
        (Join-Path ([IO.Path]::GetDirectoryName($resolvedDestination)) (
            "." +
            [IO.Path]::GetFileName($resolvedDestination) +
            "." +
            [Guid]::NewGuid().ToString("N") +
            ".previous")))
    if (-not [string]::Equals(
            [IO.Path]::GetDirectoryName($backup),
            [IO.Path]::GetDirectoryName($resolvedDestination),
            [StringComparison]::OrdinalIgnoreCase) `
        -or (Test-Path -LiteralPath $backup)) {
        throw "The Unity package backup path is unsafe."
    }
    [IO.Directory]::Move($resolvedDestination, $backup)
    try {
        [IO.Directory]::Move($resolvedStaged, $resolvedDestination)
    }
    catch {
        if (-not (Test-Path -LiteralPath $resolvedDestination) `
            -and (Test-Path -LiteralPath $backup -PathType Container)) {
            [IO.Directory]::Move($backup, $resolvedDestination)
        }
        throw
    }

    try {
        Remove-Item -LiteralPath $backup -Recurse -Force
    }
    catch {
        Write-Warning "The previous Unity package backup could not be removed: '$backup'."
    }
}

New-Item -ItemType Directory -Path $artifactRoot -Force | Out-Null
$artifactRootItem = Get-Item -LiteralPath $artifactRoot
if (($artifactRootItem.Attributes `
        -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw "The Unity artifacts directory must not be a filesystem link."
}
try {
$buildArguments = @(
    "build",
    $smokeProject,
    "-c",
    $Configuration)
if ($IncludeSymbols) {
    $buildArguments += @(
        "--no-incremental",
        "-p:DebugType=portable",
        "-p:DebugSymbols=true",
        "-p:EnableSourceLink=false")
}
& dotnet @buildArguments
if ($LASTEXITCODE -ne 0) {
    throw "Unity compile-smoke build failed with exit code $LASTEXITCODE."
}

New-Item -ItemType Directory -Path $outputPath | Out-Null
Get-ChildItem -LiteralPath $templatePath -Force | ForEach-Object {
    Copy-Item -LiteralPath $_.FullName `
        -Destination $outputPath -Recurse -Force
}
Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") `
    -Destination (Join-Path $outputPath "LICENSE.md") -Force
$pluginsPath = Join-Path $outputPath "Runtime\Plugins"
New-Item -ItemType Directory -Path $pluginsPath -Force | Out-Null

$dependencyAssemblies = @(
    "GameAgent.Protocol.dll",
    "GameAgent.Core.dll",
    "GameAgent.Generation.dll",
    "GameAgent.Persistence.dll",
    "GameAgent.Providers.Anthropic.dll",
    "GameAgent.Providers.Native.dll",
    "GameAgent.Providers.OpenAICompatible.dll",
    "GameAgent.Providers.MediaHttp.dll",
    "Microsoft.Bcl.AsyncInterfaces.dll",
    "System.Buffers.dll",
    "System.Memory.dll",
    "System.Numerics.Vectors.dll",
    "System.Runtime.CompilerServices.Unsafe.dll",
    "System.Text.Encodings.Web.dll",
    "System.Text.Json.dll",
    "System.Threading.Tasks.Extensions.dll",
    "GameAgent.Runtime.dll",
    "GameAgent.Remote.Client.dll",
    "GameAgent.Simulation.dll",
    "GameAgent.Workflow.dll"
)

$depsPath = Join-Path $smokeOutput "GameAgent.Unity.CompileSmoke.deps.json"
if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
    throw "Unity compile-smoke dependency manifest is missing: '$depsPath'."
}
$deps = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
$runtimeTargetName = $deps.runtimeTarget.name
$runtimeTarget = $deps.targets.PSObject.Properties[$runtimeTargetName].Value
if ($null -eq $runtimeTarget) {
    throw "Unity compile-smoke dependency manifest has no runtime target."
}
$resolvedRuntimeAssemblies = @(
    $runtimeTarget.PSObject.Properties |
        ForEach-Object {
            $_.Value.runtime.PSObject.Properties |
                ForEach-Object {
                    [IO.Path]::GetFileName($_.Name)
                }
        } |
        Where-Object {
            $_ -ne "GameAgent.Unity.CompileSmoke.dll"
        } |
        Sort-Object -Unique)
$expectedRuntimeAssemblies = @($dependencyAssemblies | Sort-Object -Unique)
if ([string]::Join(
        "`n",
        $resolvedRuntimeAssemblies) -cne
    [string]::Join(
        "`n",
        $expectedRuntimeAssemblies)) {
    throw "The UPM dependency allowlist does not match the resolved runtime closure."
}

foreach ($assembly in $dependencyAssemblies) {
    $source = Join-Path $smokeOutput $assembly
    if (-not (Test-Path -LiteralPath $source)) {
        throw "Required managed dependency is missing: '$source'."
    }

    Copy-Item -LiteralPath $source -Destination $pluginsPath -Force
}

if ($IncludeSymbols) {
    foreach ($symbol in @(
            "GameAgent.Protocol.pdb",
            "GameAgent.Core.pdb",
            "GameAgent.Generation.pdb",
            "GameAgent.Persistence.pdb",
            "GameAgent.Providers.Anthropic.pdb",
            "GameAgent.Providers.Native.pdb",
            "GameAgent.Providers.OpenAICompatible.pdb",
            "GameAgent.Providers.MediaHttp.pdb",
            "GameAgent.Runtime.pdb",
            "GameAgent.Remote.Client.pdb",
            "GameAgent.Simulation.pdb",
            "GameAgent.Workflow.pdb")) {
        $source = Join-Path $smokeOutput $symbol
        if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
            throw "Required managed symbol is missing: '$source'."
        }

        Copy-Item -LiteralPath $source `
            -Destination $pluginsPath -Force
    }
}

function Get-StableGuid {
    param([Parameter(Mandatory = $true)][string]$RelativePath)

    $normalized = $RelativePath.Replace("\", "/").ToLowerInvariant()
    $bytes = [Text.Encoding]::UTF8.GetBytes(
        "game-agent-unity:" + $normalized)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $hex = [BitConverter]::ToString($hash) -replace "-", ""
    return $hex.Substring(0, 32).ToLowerInvariant()
}

function Write-MetaFile {
    param(
        [Parameter(Mandatory = $true)][string]$AssetPath,
        [Parameter(Mandatory = $true)][bool]$IsDirectory
    )

    $relative = $AssetPath.Substring($outputPath.Length)
    $relative = $relative.TrimStart(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
    $guid = Get-StableGuid $relative
    $metaPath = $AssetPath + ".meta"

    if ($IsDirectory) {
        $content = @"
fileFormatVersion: 2
guid: $guid
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
"@
    }
    elseif ($AssetPath.EndsWith(
            ".cs",
            [StringComparison]::OrdinalIgnoreCase)) {
        $content = @"
fileFormatVersion: 2
guid: $guid
MonoImporter:
  externalObjects: {}
  serializedVersion: 2
  defaultReferences: []
  executionOrder: 0
  icon: {instanceID: 0}
  userData:
  assetBundleName:
  assetBundleVariant:
"@
    }
    elseif ($AssetPath.EndsWith(
            ".asmdef",
            [StringComparison]::OrdinalIgnoreCase)) {
        $content = @"
fileFormatVersion: 2
guid: $guid
AssemblyDefinitionImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
"@
    }
    elseif ($AssetPath.EndsWith(
            ".dll",
            [StringComparison]::OrdinalIgnoreCase) `
        -or $AssetPath.EndsWith(
            ".pdb",
            [StringComparison]::OrdinalIgnoreCase)) {
        $content = @"
fileFormatVersion: 2
guid: $guid
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 0
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 1
      settings: {}
  userData:
  assetBundleName:
  assetBundleVariant:
"@
    }
    else {
        $content = @"
fileFormatVersion: 2
guid: $guid
DefaultImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
"@
    }

    [IO.File]::WriteAllText(
        $metaPath,
        $content.Replace("`r`n", "`n") + "`n",
        [Text.UTF8Encoding]::new($false))
}

$assetRoots = @("Runtime", "Tests", "Samples~")
foreach ($assetRootName in $assetRoots) {
    $assetRoot = Join-Path $outputPath $assetRootName
    if (-not (Test-Path -LiteralPath $assetRoot)) {
        continue
    }

    Write-MetaFile -AssetPath $assetRoot -IsDirectory $true
    Get-ChildItem -LiteralPath $assetRoot -Recurse -Force |
        Sort-Object FullName |
        ForEach-Object {
            if (-not $_.Name.EndsWith(
                    ".meta",
                    [StringComparison]::OrdinalIgnoreCase)) {
                Write-MetaFile `
                    -AssetPath $_.FullName `
                    -IsDirectory $_.PSIsContainer
            }
        }
}

$checksums = foreach ($assembly in $dependencyAssemblies) {
    $path = Join-Path $pluginsPath $assembly
    $hashValue = (Get-FileHash `
        -LiteralPath $path -Algorithm SHA256).Hash
    $hash = $hashValue.ToLowerInvariant()
    "$hash  Runtime/Plugins/$assembly"
}
[IO.File]::WriteAllLines(
    (Join-Path $outputPath "SHA256SUMS"),
    $checksums,
    [Text.UTF8Encoding]::new($false))

$manifest = Get-Content `
    -LiteralPath (Join-Path $outputPath "package.json") `
    -Raw | ConvertFrom-Json
if ($manifest.name -ne "com.gameagent.runtime.unity") {
    throw "The staged UPM manifest has an unexpected package name."
}

Publish-DirectoryAtomically `
    -StagedPath $outputPath `
    -Destination $publishedOutputPath
Write-Host "UPM artifact ready: $publishedOutputPath"
}
finally {
    $resolvedStagingPath = [IO.Path]::GetFullPath($outputPath)
    $requiredPrefix = $artifactRoot.TrimEnd(
        [IO.Path]::DirectorySeparatorChar) `
        + [IO.Path]::DirectorySeparatorChar
    $safeStagingName = [IO.Path]::GetFileName(
        $resolvedStagingPath).StartsWith(
            ".com.gameagent.runtime.unity.",
            [StringComparison]::Ordinal) `
        -and [IO.Path]::GetFileName(
            $resolvedStagingPath).EndsWith(
            ".staging",
            [StringComparison]::Ordinal)
    if ($safeStagingName `
        -and $resolvedStagingPath.StartsWith(
            $requiredPrefix,
            [StringComparison]::OrdinalIgnoreCase) `
        -and (Test-Path -LiteralPath $resolvedStagingPath -PathType Container)) {
        Remove-Item -LiteralPath $resolvedStagingPath -Recurse -Force
    }
}
