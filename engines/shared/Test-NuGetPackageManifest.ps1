[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Path,

    [Parameter(Mandatory)]
    [string]$ExpectedVersion,

    [Parameter(Mandatory)]
    [string]$ExpectedCommit
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($ExpectedVersion -notmatch
    '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw 'The expected package version is invalid.'
}
if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'The expected package commit must be a complete commit id.'
}

$expectedPackageIds = @(
    'GameAgent.Core',
    'GameAgent.Persistence',
    'GameAgent.Protocol',
    'GameAgent.Providers.Anthropic',
    'GameAgent.Providers.OpenAICompatible',
    'GameAgent.Runtime',
    'GameAgent.Testing',
    'GameAgent.Workflow')
$expectedInternalDependencies = @{
    'GameAgent.Core' = @('GameAgent.Protocol')
    'GameAgent.Persistence' = @(
        'GameAgent.Core',
        'GameAgent.Protocol')
    'GameAgent.Protocol' = @()
    'GameAgent.Providers.Anthropic' = @('GameAgent.Core')
    'GameAgent.Providers.OpenAICompatible' = @('GameAgent.Core')
    'GameAgent.Runtime' = @(
        'GameAgent.Core',
        'GameAgent.Persistence',
        'GameAgent.Protocol',
        'GameAgent.Providers.OpenAICompatible')
    'GameAgent.Testing' = @(
        'GameAgent.Core',
        'GameAgent.Protocol')
    'GameAgent.Workflow' = @('GameAgent.Core')
}
$packageRoot = [IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath $Path))
$packages = @(
    Get-ChildItem -LiteralPath $packageRoot -File -Filter '*.nupkg')
if ($packages.Count -ne $expectedPackageIds.Count) {
    throw 'The package output does not contain the expected package count.'
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
foreach ($expectedId in $expectedPackageIds) {
    $expectedName = "$expectedId.$ExpectedVersion.nupkg"
    $matches = @(
        $packages |
            Where-Object {
                [string]::Equals(
                    $_.Name,
                    $expectedName,
                    [StringComparison]::Ordinal)
            })
    if ($matches.Count -ne 1) {
        throw 'An expected package is missing or duplicated.'
    }

    $archive = [IO.Compression.ZipFile]::OpenRead($matches[0].FullName)
    try {
        $expectedEntries = @(
            '[Content_Types].xml',
            '_rels/.rels',
            "$expectedId.nuspec",
            "lib/netstandard2.1/$expectedId.dll",
            'package/services/metadata/core-properties/core.psmdcp',
            'README.md')
        $actualEntries = @(
            $archive.Entries |
                ForEach-Object { $_.FullName } |
                Sort-Object)
        if ($archive.Entries.Count -ne $expectedEntries.Count -or
            [string]::Join(
                "`n",
                $actualEntries) -cne
            [string]::Join(
                "`n",
                @($expectedEntries | Sort-Object))) {
            throw (
                'A package contains an unexpected or executable asset.')
        }

        $readmes = @(
            $archive.Entries |
                Where-Object {
                    [string]::Equals(
                        $_.FullName,
                        'README.md',
                        [StringComparison]::Ordinal)
                })
        $nuspecs = @(
            $archive.Entries |
                Where-Object {
                    $_.FullName.EndsWith(
                        '.nuspec',
                        [StringComparison]::OrdinalIgnoreCase)
                })
        if ($readmes.Count -ne 1 -or $nuspecs.Count -ne 1) {
            throw 'A package must contain one root README and one manifest.'
        }

        $settings = New-Object Xml.XmlReaderSettings
        $settings.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $settings.XmlResolver = $null
        $stream = $nuspecs[0].Open()
        $reader = $null
        try {
            $reader = [Xml.XmlReader]::Create($stream, $settings)
            $document = New-Object Xml.XmlDocument
            $document.XmlResolver = $null
            $document.Load($reader)
        }
        finally {
            if ($null -ne $reader) {
                $reader.Dispose()
            }
            $stream.Dispose()
        }

        $metadata = $document.SelectSingleNode(
            "/*[local-name()='package']/*[local-name()='metadata']")
        if ($null -eq $metadata) {
            throw 'A package manifest is missing metadata.'
        }
        $idNode = $metadata.SelectSingleNode("*[local-name()='id']")
        $versionNode = $metadata.SelectSingleNode("*[local-name()='version']")
        $readmeNode = $metadata.SelectSingleNode("*[local-name()='readme']")
        $repositoryNode = $metadata.SelectSingleNode(
            "*[local-name()='repository']")
        if ($null -eq $idNode -or $idNode.InnerText -ne $expectedId -or
            $null -eq $versionNode -or
            $versionNode.InnerText -ne $ExpectedVersion -or
            $null -eq $readmeNode -or
            $readmeNode.InnerText -ne 'README.md' -or
            $null -eq $repositoryNode -or
            $repositoryNode.GetAttribute('commit') -ne $ExpectedCommit) {
            throw 'A package manifest does not match release metadata.'
        }

        $exactVersion = "[$ExpectedVersion]"
        $internalDependencies = New-Object 'Collections.Generic.List[string]'
        foreach ($dependency in @(
                $metadata.SelectNodes(
                    ".//*[local-name()='dependency']"))) {
            $dependencyId = $dependency.GetAttribute('id')
            if ($dependencyId.StartsWith(
                    'GameAgent.',
                    [StringComparison]::Ordinal)) {
                if ($dependency.GetAttribute('version') -ne $exactVersion) {
                    throw 'An internal package dependency is not exactly pinned.'
                }
                $internalDependencies.Add($dependencyId)
            }
        }
        $actualDependencySet = @($internalDependencies | Sort-Object)
        $expectedDependencySet = @(
            $expectedInternalDependencies[$expectedId] | Sort-Object)
        if ([string]::Join("`n", $actualDependencySet) -cne
            [string]::Join("`n", $expectedDependencySet)) {
            throw 'An internal package dependency set is incomplete.'
        }
    }
    finally {
        $archive.Dispose()
    }
}

Write-Output 'NUGET_PACKAGE_MANIFEST_PASS'
