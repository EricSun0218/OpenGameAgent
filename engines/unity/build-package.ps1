[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $UnityManagedDir,
    [string] $Version = '0.3.0-alpha.3',
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

$UnityManagedDir = [IO.Path]::GetFullPath($UnityManagedDir)
if (-not (Test-Path -LiteralPath (Join-Path $UnityManagedDir 'UnityEngine.CoreModule.dll') -PathType Leaf)) {
    throw 'UnityManagedDir must contain UnityEngine.CoreModule.dll.'
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'Version must be a semantic version.'
}

$engineRoot = Split-Path -Parent $PSCommandPath
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $engineRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\unity'
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$packagePath = Join-Path $outputRoot 'com.opengameagent.runtime'
if ($packagePath -eq $outputRoot -or -not $packagePath.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The package output path is unsafe.'
}

$project = Join-Path $engineRoot 'OpenGameAgent.Unity.csproj'
& dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Unity package restore failed.' }
& dotnet build $project -c Release --no-restore "-p:UnityManagedDir=$UnityManagedDir" "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Unity package build failed.' }

if (Test-Path -LiteralPath $packagePath) {
    Remove-Item -LiteralPath $packagePath -Recurse -Force
}

New-Item -ItemType Directory -Path $outputRoot -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $engineRoot 'Packages\com.opengameagent.runtime') -Destination $packagePath -Recurse
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $packagePath 'LICENSE.md') -Force
$manifestPath = Join-Path $packagePath 'package.json'
$manifest = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
$manifest.version = $Version
$manifest | ConvertTo-Json -Depth 16 | Set-Content -LiteralPath $manifestPath -Encoding utf8NoBOM

$plugins = Join-Path $packagePath 'Runtime\Plugins'
New-Item -ItemType Directory -Path $plugins -Force | Out-Null
$pluginFolderMeta = @'
fileFormatVersion: 2
guid: 44b5a2f060af4f47a7b6382bbf4c91e3
folderAsset: yes
DefaultImporter:
  externalObjects: {}
  userData:
  assetBundleName:
  assetBundleVariant:
'@
$pluginFolderMeta | Set-Content -LiteralPath ($plugins + '.meta') -Encoding utf8NoBOM
$buildOutput = Join-Path $engineRoot 'bin\Release\netstandard2.1'
$assemblies = [ordered]@{
    'OpenGameAgent.Attachments.dll' = '01b0c910bb244a64ba3d85bb66348656'
    'OpenGameAgent.Kernel.dll' = 'b62e147235cd4b60bf2b3ec44621214f'
    'OpenGameAgent.dll' = 'b8a23b387de04f5d8b59f0d59028cb6d'
    'OpenGameAgent.Client.dll' = '36eb78a2d19d407fbe9308d746ed42fd'
    'Microsoft.Bcl.AsyncInterfaces.dll' = '9b8649054b3045538a0c970541601e20'
    'System.Buffers.dll' = '8fd925030a224e299e0360380b4cce95'
    'System.Memory.dll' = 'f3ff769890dd4a0984c60e9fe60a057b'
    'System.Numerics.Vectors.dll' = 'c2ebd4c7e9d442e88c3c3f1834a2bc61'
    'System.Runtime.CompilerServices.Unsafe.dll' = 'cfde63bac58a47c1ae4ab26071b7b4c3'
    'System.Text.Encodings.Web.dll' = '22152543142c4c3fb92fad3926faf738'
    'System.Text.Json.dll' = '05248059895240c697cf289fb679c338'
    'System.Threading.Tasks.Extensions.dll' = 'c47f17f452314244b691d2fa0b7d1d1a'
}
foreach ($assembly in $assemblies.Keys) {
    $source = Join-Path $buildOutput $assembly
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required Unity assembly '$assembly' is missing."
    }

    $destination = Join-Path $plugins $assembly
    Copy-Item -LiteralPath $source -Destination $destination
    $pluginMeta = @"
fileFormatVersion: 2
guid: $($assemblies[$assembly])
PluginImporter:
  externalObjects: {}
  serializedVersion: 2
  iconMap: {}
  executionOrder: {}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 1
  isExplicitlyReferenced: 0
  validateReferences: 1
  platformData:
  - first:
      Any:
    second:
      enabled: 1
      settings: {}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      Windows Store Apps: WindowsStoreApps
    second:
      enabled: 0
      settings:
        CPU: AnyCPU
  userData:
  assetBundleName:
  assetBundleVariant:
"@
    $pluginMeta | Set-Content -LiteralPath ($destination + '.meta') -Encoding utf8NoBOM
}

$packagePath
