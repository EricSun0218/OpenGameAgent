[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactPath,

    [string]$ExpectedVersion,

    [string]$WorkingPath,

    [switch]$ExpectSymbols
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Test-WindowsPortablePathSegment {
    param([Parameter(Mandatory)][string]$Segment)

    if ([string]::IsNullOrEmpty($Segment) `
        -or $Segment.Length -gt 255 `
        -or $Segment -match '[<>:"/\\|?*\x00-\x1f]' `
        -or $Segment -match '[. ]$') {
        return $false
    }

    $stem = $Segment.Split('.')[0].TrimEnd([char[]]@(' ', '.'))
    return $stem -notmatch (
        '^(?i:CON|PRN|AUX|NUL|CLOCK[$]|CONIN[$]|CONOUT[$]|' +
        'COM[0-9¹²³]|LPT[0-9¹²³])$')
}

function Get-StableUnityAssetGuid {
    param([Parameter(Mandatory)][string]$RelativePath)

    $normalized = $RelativePath.Replace('\', '/').ToLowerInvariant()
    $bytes = [Text.Encoding]::UTF8.GetBytes(
        'game-agent-unity:' + $normalized)
    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $hash = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $hex = [BitConverter]::ToString($hash) -replace '-', ''
    return $hex.Substring(0, 32).ToLowerInvariant()
}

function Get-ExpectedPluginImporterMeta {
    param([Parameter(Mandatory)][string]$RelativePath)

    $guid = Get-StableUnityAssetGuid -RelativePath $RelativePath
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
    return $content.Replace("`r`n", "`n") + "`n"
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and
    $ExpectedVersion -notmatch
        '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw 'The expected Unity package version is invalid.'
}

$artifact = Get-Item -LiteralPath (
    Resolve-Path -LiteralPath $ArtifactPath)
if (-not $artifact.PSIsContainer -or
    ($artifact.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The Unity package artifact must be a regular directory.'
}
$children = @(
    Get-ChildItem -LiteralPath $artifact.FullName -Recurse -Force)
if ($children.Count -eq 0 -or $children.Count -gt 10000) {
    throw 'The Unity package artifact has an invalid item count.'
}
foreach ($child in $children) {
    if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The Unity package artifact contains a filesystem link.'
    }

    if (-not (Test-WindowsPortablePathSegment -Segment $child.Name)) {
        throw 'The Unity package artifact contains a non-portable Windows path.'
    }
}

$required = @(
    'package.json',
    'LICENSE.md',
    'THIRD PARTY NOTICES.md',
    'Runtime/GameAgent.Unity.asmdef',
    'Runtime/Plugins/GameAgent.Protocol.dll',
    'Runtime/Plugins/GameAgent.Core.dll',
    'Runtime/Plugins/GameAgent.Compatibility.dll',
    'Runtime/Plugins/GameAgent.Persistence.dll',
    'Runtime/Plugins/GameAgent.Providers.Anthropic.dll',
    'Runtime/Plugins/GameAgent.Providers.OpenAICompatible.dll',
    'Runtime/Plugins/GameAgent.Runtime.dll',
    'Runtime/Plugins/GameAgent.Workflow.dll',
    'Runtime/Plugins/GameAgent.World.dll',
    'Runtime/Plugins/System.Text.Json.dll',
    'Tests/Runtime/UnityDurableGateScenario.cs',
    'Samples~/StructuredToolLoop/StructuredToolLoopSample.cs',
    'Samples~/InteractiveWorld/World/world.json',
    'Samples~/InteractiveWorld/World/interactions.json',
    'Documentation~/index.md',
    'Documentation~/Schemas/world-v1/world.schema.json',
    'Documentation~/Schemas/world-v1/interactions.schema.json',
    'SHA256SUMS')
foreach ($relativePath in $required) {
    $nativePath = $relativePath.Replace(
        '/',
        [IO.Path]::DirectorySeparatorChar)
    $path = Join-Path $artifact.FullName $nativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The Unity package artifact is missing '$relativePath'."
    }
}

$expectedWorldSchemaNames = @(
    'agents.schema.json',
    'clocks.schema.json',
    'events.schema.json',
    'interactions.schema.json',
    'knowledge.schema.json',
    'native-world-common.schema.json',
    'numerics.schema.json',
    'world.schema.json') | Sort-Object
$worldSchemaPath = Join-Path $artifact.FullName `
    'Documentation~\Schemas\world-v1'
$actualWorldSchemaNames = @(
    Get-ChildItem `
        -LiteralPath $worldSchemaPath `
        -Filter '*.json' `
        -File `
        -Recurse |
        ForEach-Object {
            $_.FullName.Substring($worldSchemaPath.Length + 1).Replace(
                '\',
                '/')
        } |
        Sort-Object)
if ([string]::Join("`n", $actualWorldSchemaNames) -cne
    [string]::Join("`n", $expectedWorldSchemaNames)) {
    throw 'The Unity package native-world schema set is incomplete or unexpected.'
}
$unexpectedWorldSchemaFiles = @(
    Get-ChildItem -LiteralPath $worldSchemaPath -File -Recurse |
        Where-Object {
            $relative = $_.FullName.Substring(
                $worldSchemaPath.Length + 1).Replace(
                    '\',
                    '/')
            $relative -cnotin $expectedWorldSchemaNames -and
            $relative -cne 'README.md'
        })
if ($unexpectedWorldSchemaFiles.Count -ne 0) {
    throw 'The Unity package native-world schema directory contains unexpected files.'
}

$expectedWorldExampleNames = @(
    'agents.json',
    'clocks.json',
    'events.json',
    'interactions.json',
    'knowledge.json',
    'numerics.json',
    'world.json') | Sort-Object
$worldExamplePath = Join-Path $artifact.FullName `
    'Samples~\InteractiveWorld\World'
$actualWorldExampleNames = @(
    Get-ChildItem `
        -LiteralPath $worldExamplePath `
        -Filter '*.json' `
        -File `
        -Recurse |
        ForEach-Object {
            $_.FullName.Substring($worldExamplePath.Length + 1).Replace(
                '\',
                '/')
        } |
        Sort-Object)
if ([string]::Join("`n", $actualWorldExampleNames) -cne
    [string]::Join("`n", $expectedWorldExampleNames)) {
    throw 'The Unity package native-world example set is incomplete or unexpected.'
}
$unexpectedWorldExampleFiles = @(
    Get-ChildItem -LiteralPath $worldExamplePath -File -Recurse |
        Where-Object {
            $relative = $_.FullName.Substring(
                $worldExamplePath.Length + 1).Replace(
                    '\',
                    '/')
            $relative -cnotin $expectedWorldExampleNames -and
            $relative -cnotin @(
                $expectedWorldExampleNames |
                    ForEach-Object { $_ + '.meta' })
        })
if ($unexpectedWorldExampleFiles.Count -ne 0) {
    throw 'The Unity package native-world example directory contains unexpected files.'
}

$package = Get-Content -LiteralPath (
    Join-Path $artifact.FullName 'package.json') -Raw |
    ConvertFrom-Json
if ([string]$package.name -ne 'com.gameagent.runtime.unity' -or
    [string]::IsNullOrWhiteSpace([string]$package.version) -or
    (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) -and
        [string]$package.version -ne $ExpectedVersion)) {
    throw 'The Unity package manifest does not match the release.'
}

$licenseText = Get-Content -LiteralPath (
    Join-Path $artifact.FullName 'LICENSE.md') -Raw
if ($licenseText -notmatch 'Apache License' -or
    $licenseText -notmatch 'TERMS AND CONDITIONS') {
    throw 'The Unity package artifact has an incomplete license.'
}
$noticeText = Get-Content -LiteralPath (
    Join-Path $artifact.FullName 'THIRD PARTY NOTICES.md') -Raw
if ($noticeText -notmatch 'Permission is hereby granted') {
    throw 'The Unity package artifact has incomplete third-party notices.'
}

$checksumPath = Join-Path $artifact.FullName 'SHA256SUMS'
$checksumEntries = [Collections.Generic.HashSet[string]]::new(
    [StringComparer]::Ordinal)
foreach ($line in Get-Content -LiteralPath $checksumPath) {
    if ($line -notmatch '^([0-9a-f]{64})  (Runtime/Plugins/.+[.]dll)$') {
        throw "The Unity package checksum entry is invalid: '$line'."
    }

    $relativePath = $Matches[2]
    $nativePath = $relativePath.Replace(
        '/',
        [IO.Path]::DirectorySeparatorChar)
    $path = Join-Path $artifact.FullName $nativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "The Unity package checksum target is missing: '$relativePath'."
    }

    $actual = (
        Get-FileHash -LiteralPath $path -Algorithm SHA256
    ).Hash.ToLowerInvariant()
    if ($actual -ne $Matches[1] -or
        -not $checksumEntries.Add($relativePath)) {
        throw "The Unity package checksum is invalid: '$relativePath'."
    }
}

$plugins = Join-Path $artifact.FullName (
    'Runtime' + [IO.Path]::DirectorySeparatorChar + 'Plugins')
$expectedPluginNames = @(
    'GameAgent.Compatibility.dll',
    'GameAgent.Core.dll',
    'GameAgent.Persistence.dll',
    'GameAgent.Protocol.dll',
    'GameAgent.Providers.Anthropic.dll',
    'GameAgent.Providers.OpenAICompatible.dll',
    'GameAgent.Runtime.dll',
    'GameAgent.Workflow.dll',
    'GameAgent.World.dll',
    'Microsoft.Bcl.AsyncInterfaces.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Text.Encodings.Web.dll',
    'System.Text.Json.dll',
    'System.Threading.Tasks.Extensions.dll')
$expectedPluginPaths = @(
    $expectedPluginNames |
        ForEach-Object { 'Runtime/Plugins/' + $_ } |
        Sort-Object)
$artifactFiles = @($children | Where-Object { -not $_.PSIsContainer })
$actualPluginPaths = @(
    $artifactFiles |
        Where-Object {
            [string]::Equals(
                $_.Extension,
                '.dll',
                [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            [IO.Path]::GetRelativePath(
                $artifact.FullName,
                $_.FullName).Replace('\', '/')
        } |
        Sort-Object)
if ([string]::Join("`n", $actualPluginPaths) -cne
    [string]::Join("`n", $expectedPluginPaths)) {
    throw 'The Unity package global DLL set is invalid.'
}

$expectedSymbolNames = @(
    'GameAgent.Compatibility.pdb',
    'GameAgent.Core.pdb',
    'GameAgent.Persistence.pdb',
    'GameAgent.Protocol.pdb',
    'GameAgent.Providers.Anthropic.pdb',
    'GameAgent.Providers.OpenAICompatible.pdb',
    'GameAgent.Runtime.pdb',
    'GameAgent.Workflow.pdb',
    'GameAgent.World.pdb')
$expectedSymbolPaths = @()
if ($ExpectSymbols) {
    $expectedSymbolPaths = @(
        $expectedSymbolNames |
            ForEach-Object { 'Runtime/Plugins/' + $_ } |
            Sort-Object)
}
$actualSymbolPaths = @(
    $artifactFiles |
        Where-Object {
            [string]::Equals(
                $_.Extension,
                '.pdb',
                [StringComparison]::OrdinalIgnoreCase)
        } |
        ForEach-Object {
            [IO.Path]::GetRelativePath(
                $artifact.FullName,
                $_.FullName).Replace('\', '/')
        } |
        Sort-Object)
if ([string]::Join("`n", [string[]]$actualSymbolPaths) -cne
    [string]::Join("`n", [string[]]$expectedSymbolPaths)) {
    throw 'The Unity package global PDB set is invalid.'
}

$expectedDllMetaPaths = @(
    $expectedPluginPaths |
        ForEach-Object { $_ + '.meta' } |
        Sort-Object)
$actualDllMetaPaths = @(
    $artifactFiles |
        ForEach-Object {
            [IO.Path]::GetRelativePath(
                $artifact.FullName,
                $_.FullName).Replace('\', '/')
        } |
        Where-Object {
            $_.EndsWith(
                '.dll.meta',
                [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object)
if ([string]::Join("`n", $actualDllMetaPaths) -cne
    [string]::Join("`n", $expectedDllMetaPaths)) {
    throw 'The Unity package global DLL metadata set is invalid.'
}

$expectedPdbMetaPaths = @(
    $expectedSymbolPaths |
        ForEach-Object { $_ + '.meta' } |
        Sort-Object)
$actualPdbMetaPaths = @(
    $artifactFiles |
        ForEach-Object {
            [IO.Path]::GetRelativePath(
                $artifact.FullName,
                $_.FullName).Replace('\', '/')
        } |
        Where-Object {
            $_.EndsWith(
                '.pdb.meta',
                [StringComparison]::OrdinalIgnoreCase)
        } |
        Sort-Object)
if ([string]::Join("`n", [string[]]$actualPdbMetaPaths) -cne
    [string]::Join("`n", [string[]]$expectedPdbMetaPaths)) {
    throw 'The Unity package global PDB metadata set is invalid.'
}

foreach ($assetRelativePath in @(
        $expectedPluginPaths
        $expectedSymbolPaths)) {
    $metaRelativePath = $assetRelativePath + '.meta'
    $metaPath = Join-Path $artifact.FullName (
        $metaRelativePath.Replace(
            '/',
            [IO.Path]::DirectorySeparatorChar))
    $expectedMeta = Get-ExpectedPluginImporterMeta `
        -RelativePath $assetRelativePath
    $actualMetaBytes = [IO.File]::ReadAllBytes($metaPath)
    $expectedMetaBytes = [Text.UTF8Encoding]::new($false).GetBytes(
        $expectedMeta)
    if (-not [string]::Equals(
            [Convert]::ToBase64String($actualMetaBytes),
            [Convert]::ToBase64String($expectedMetaBytes),
            [StringComparison]::Ordinal)) {
        throw "The Unity PluginImporter metadata is invalid: '$metaRelativePath'."
    }
}

$unsupportedExecutableItems = @(
    $children |
        Where-Object {
            $_.Name -match
                '(?i)(?:[.]exe|[.]dylib|[.]bundle|[.]so(?:[.][0-9]+)*)$'
        })
if ($unsupportedExecutableItems.Count -ne 0) {
    throw 'The Unity package contains an unsupported executable binary.'
}

$pluginDlls = @(
    Get-ChildItem `
        -LiteralPath $plugins `
        -Filter '*.dll' `
        -File `
        -Recurse)
$actualPluginNames = @($pluginDlls.Name | Sort-Object)
if ([string]::Join("`n", $actualPluginNames) -cne
    [string]::Join("`n", @($expectedPluginNames | Sort-Object))) {
    throw 'The Unity package runtime assembly closure is invalid.'
}
foreach ($pluginDll in $pluginDlls) {
    if (-not $pluginDll.DirectoryName.Equals(
            $plugins,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Unity runtime assemblies must be at the plugin root.'
    }
    $relativePath = 'Runtime/Plugins/' + $pluginDll.Name
    if (-not $checksumEntries.Contains($relativePath)) {
        throw "A Unity package assembly has no checksum: '$relativePath'."
    }
    if (-not $pluginDll.Name.StartsWith(
            'GameAgent.',
            [StringComparison]::Ordinal)) {
        $noticeName = [IO.Path]::GetFileNameWithoutExtension($pluginDll.Name)
        if ($noticeText.IndexOf(
                ('`' + $noticeName + '`'),
                [StringComparison]::Ordinal) -lt 0) {
            throw 'A bundled Unity dependency is absent from the notices.'
        }
    }
}
if ($checksumEntries.Count -ne $pluginDlls.Count) {
    throw 'The Unity package checksum set does not match its assemblies.'
}

$pluginSymbols = @(
    Get-ChildItem `
        -LiteralPath $plugins `
        -Filter '*.pdb' `
        -File `
        -Recurse)
$expectedSymbols = @()
if ($ExpectSymbols) {
    $expectedSymbols = @($expectedSymbolNames | Sort-Object)
}
$actualSymbols = @(
    $pluginSymbols |
        ForEach-Object { $_.Name } |
        Sort-Object)
if ([string]::Join("`n", [string[]]$actualSymbols) -cne
    [string]::Join("`n", [string[]]$expectedSymbols)) {
    throw 'The Unity package managed-symbol set is invalid.'
}
foreach ($pluginSymbol in $pluginSymbols) {
    if (-not $pluginSymbol.DirectoryName.Equals(
            $plugins,
            [StringComparison]::OrdinalIgnoreCase) -or
        -not (Test-Path -LiteralPath (
            $pluginSymbol.FullName + '.meta') -PathType Leaf)) {
        throw 'Unity managed symbols must be at the plugin root with metadata.'
    }
}

$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..\..'))
& (Join-Path $repositoryRoot `
    'engines\shared\Test-ReleaseArtifactPrivacy.ps1') `
    -Path $artifact.FullName

$runtimeRoot = Join-Path $artifact.FullName 'Runtime'
$runtimeSources = Get-ChildItem `
    -LiteralPath $runtimeRoot `
    -Filter '*.cs' `
    -Recurse `
    -File
foreach ($source in $runtimeSources) {
    $content = Get-Content -LiteralPath $source.FullName -Raw
    if ($content -match 'class\s+HeadlessAgentRuntime') {
        throw 'The Unity SDK artifact duplicates the agent core.'
    }
}

$removeWorkingPath = [string]::IsNullOrWhiteSpace($WorkingPath)
if ($removeWorkingPath) {
    $WorkingPath = Join-Path (
        [IO.Path]::GetTempPath()
    ) ('game-agent-unity-consumer-' + [guid]::NewGuid().ToString('N'))
}
$working = [IO.Path]::GetFullPath($WorkingPath)
if ([IO.File]::Exists($working) -or
    [IO.Directory]::Exists($working)) {
    throw 'The Unity package consumer working path must not exist.'
}
$null = New-Item -ItemType Directory -Path $working
try {
    $consumerTemplate = Join-Path $repositoryRoot 'tests\UnityCompileSmoke'
    foreach ($fileName in @(
            'UnityCompileSmoke.csproj',
            'UnityEngineStubs.cs',
            'UnityEditorAndTestStubs.cs',
            'TrustedArtifactApiSmoke.cs')) {
        Copy-Item `
            -LiteralPath (Join-Path $consumerTemplate $fileName) `
            -Destination (Join-Path $working $fileName)
    }
    $consumerProject = Join-Path $working 'UnityCompileSmoke.csproj'
    & dotnet build $consumerProject `
        -c Release `
        --nologo `
        "-p:UnityArtifactPath=$($artifact.FullName)"
    if ($LASTEXITCODE -ne 0) {
        throw 'The Unity package assemblies cannot be consumed.'
    }

    $loaderTemplate = Join-Path $repositoryRoot (
        'tests\UnityArtifactLoadSmoke')
    $loaderRoot = Join-Path $working 'loader'
    $null = New-Item -ItemType Directory -Path $loaderRoot
    foreach ($fileName in @(
            'UnityArtifactLoadSmoke.csproj',
            'Program.cs')) {
        Copy-Item `
            -LiteralPath (Join-Path $loaderTemplate $fileName) `
            -Destination (Join-Path $loaderRoot $fileName)
    }
    $loaderArguments = @(
        'run',
        '--project',
        (Join-Path $loaderRoot 'UnityArtifactLoadSmoke.csproj'),
        '--configuration',
        'Release',
        '--',
        $plugins)
    & dotnet @loaderArguments
    if ($LASTEXITCODE -ne 0) {
        throw 'The Unity package runtime assembly closure cannot be loaded.'
    }
}
finally {
    if ($removeWorkingPath -and [IO.Directory]::Exists($working)) {
        Remove-Item -LiteralPath $working -Recurse -Force
    }
}

Write-Output 'UNITY_PACKAGE_ARTIFACT_PASS'
