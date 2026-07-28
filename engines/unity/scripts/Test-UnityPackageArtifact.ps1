[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactPath,

    [string]$ExpectedVersion,

    [string]$WorkingPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

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
}

$required = @(
    'package.json',
    'LICENSE.md',
    'THIRD PARTY NOTICES.md',
    'Runtime/GameAgent.Unity.asmdef',
    'Runtime/Plugins/GameAgent.Protocol.dll',
    'Runtime/Plugins/GameAgent.Core.dll',
    'Runtime/Plugins/GameAgent.Persistence.dll',
    'Runtime/Plugins/GameAgent.Providers.OpenAICompatible.dll',
    'Runtime/Plugins/GameAgent.Runtime.dll',
    'Runtime/Plugins/System.Text.Json.dll',
    'Tests/Runtime/UnityDurableGateScenario.cs',
    'Samples~/StructuredToolLoop/StructuredToolLoopSample.cs',
    'Documentation~/index.md',
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
$pluginDlls = @(
    Get-ChildItem `
        -LiteralPath $plugins `
        -Filter '*.dll' `
        -File `
        -Recurse)
$expectedPluginNames = @(
    'GameAgent.Core.dll',
    'GameAgent.Persistence.dll',
    'GameAgent.Protocol.dll',
    'GameAgent.Providers.OpenAICompatible.dll',
    'GameAgent.Runtime.dll',
    'Microsoft.Bcl.AsyncInterfaces.dll',
    'System.Buffers.dll',
    'System.Memory.dll',
    'System.Numerics.Vectors.dll',
    'System.Runtime.CompilerServices.Unsafe.dll',
    'System.Text.Encodings.Web.dll',
    'System.Text.Json.dll',
    'System.Threading.Tasks.Extensions.dll')
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
