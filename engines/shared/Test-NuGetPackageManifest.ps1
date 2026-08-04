[CmdletBinding()]
param(
    [Parameter(Mandatory, ParameterSetName = 'Validate')]
    [string]$Path,

    [Parameter(Mandatory, ParameterSetName = 'Validate')]
    [string]$ExpectedVersion,

    [Parameter(Mandatory, ParameterSetName = 'Validate')]
    [string]$ExpectedCommit,

    [Parameter(Mandatory, ParameterSetName = 'List')]
    [switch]$ListExpectedPackageIds
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$expectedPackageIds = @(
    'GameAgent.Core',
    'GameAgent.Evaluation',
    'GameAgent.Generation',
    'GameAgent.Hosting',
    'GameAgent.Observability.OpenTelemetry',
    'GameAgent.Persistence',
    'GameAgent.Protocol',
    'GameAgent.Providers.Anthropic',
    'GameAgent.Providers.MediaHttp',
    'GameAgent.Providers.Native',
    'GameAgent.Providers.OpenAICompatible',
    'GameAgent.Remote.Client',
    'GameAgent.Runtime',
    'GameAgent.Simulation',
    'GameAgent.Storage.Postgres',
    'GameAgent.Storage.Relational',
    'GameAgent.Storage.Sqlite',
    'GameAgent.Testing',
    'GameAgent.Workflow')
if ($ListExpectedPackageIds) {
    $expectedPackageIds
    return
}
if ($ExpectedVersion -notmatch
    '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$') {
    throw 'The expected package version is invalid.'
}
if ($ExpectedCommit -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'The expected package commit must be a complete commit id.'
}
$expectedInternalDependencies = @{
    'GameAgent.Core' = @('GameAgent.Protocol')
    'GameAgent.Evaluation' = @('GameAgent.Protocol')
    'GameAgent.Generation' = @(
        'GameAgent.Core',
        'GameAgent.Protocol')
    'GameAgent.Hosting' = @(
        'GameAgent.Core',
        'GameAgent.Protocol',
        'GameAgent.Runtime')
    'GameAgent.Observability.OpenTelemetry' = @('GameAgent.Core')
    'GameAgent.Persistence' = @(
        'GameAgent.Core',
        'GameAgent.Protocol')
    'GameAgent.Protocol' = @()
    'GameAgent.Providers.Anthropic' = @('GameAgent.Core')
    'GameAgent.Providers.MediaHttp' = @('GameAgent.Generation')
    'GameAgent.Providers.Native' = @('GameAgent.Core')
    'GameAgent.Providers.OpenAICompatible' = @('GameAgent.Core')
    'GameAgent.Remote.Client' = @(
        'GameAgent.Core',
        'GameAgent.Protocol')
    'GameAgent.Runtime' = @(
        'GameAgent.Core',
        'GameAgent.Persistence',
        'GameAgent.Protocol',
        'GameAgent.Providers.Native',
        'GameAgent.Providers.OpenAICompatible')
    'GameAgent.Simulation' = @(
        'GameAgent.Core',
        'GameAgent.Protocol')
    'GameAgent.Storage.Postgres' = @('GameAgent.Storage.Relational')
    'GameAgent.Storage.Relational' = @(
        'GameAgent.Core',
        'GameAgent.Protocol')
    'GameAgent.Storage.Sqlite' = @('GameAgent.Storage.Relational')
    'GameAgent.Testing' = @(
        'GameAgent.Core',
        'GameAgent.Protocol')
    'GameAgent.Workflow' = @('GameAgent.Core')
}
$targetFrameworks = @{
    'GameAgent.Hosting' = 'net8.0'
    'GameAgent.Observability.OpenTelemetry' = 'net8.0'
    'GameAgent.Storage.Postgres' = 'net8.0'
    'GameAgent.Storage.Relational' = 'net8.0'
    'GameAgent.Storage.Sqlite' = 'net8.0'
}
$packageRoot = [IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath $Path))
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$expectedLicenseBytes = [IO.File]::ReadAllBytes(
    (Join-Path $repositoryRoot 'LICENSE'))
$expectedReadmeBytes = [IO.File]::ReadAllBytes(
    (Join-Path $repositoryRoot 'docs\nuget-package-readme.md'))
$expectedLicenseText = [Text.Encoding]::UTF8.GetString($expectedLicenseBytes)
if ($expectedLicenseText.Contains("`r", [StringComparison]::Ordinal)) {
    throw 'The repository license must use canonical LF line endings.'
}
$expectedReadmeText = [Text.Encoding]::UTF8.GetString($expectedReadmeBytes)
if (@($expectedPackageIds | Where-Object {
            -not $expectedReadmeText.Contains(
                "``$_``",
                [StringComparison]::Ordinal)
        }).Count -ne 0) {
    throw 'The package README does not describe every published package.'
}
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
        $targetFramework = if ($targetFrameworks.ContainsKey($expectedId)) {
            $targetFrameworks[$expectedId]
        }
        else {
            'netstandard2.1'
        }
        $expectedEntries = @(
            '[Content_Types].xml',
            '_rels/.rels',
            "$expectedId.nuspec",
            "lib/$targetFramework/$expectedId.dll",
            'package/services/metadata/core-properties/core.psmdcp',
            'LICENSE',
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
        $licenses = @(
            $archive.Entries |
                Where-Object {
                    [string]::Equals(
                        $_.FullName,
                        'LICENSE',
                        [StringComparison]::Ordinal)
                })
        $nuspecs = @(
            $archive.Entries |
                Where-Object {
                    $_.FullName.EndsWith(
                        '.nuspec',
                        [StringComparison]::OrdinalIgnoreCase)
                })
        if ($readmes.Count -ne 1 -or
            $licenses.Count -ne 1 -or
            $nuspecs.Count -ne 1) {
            throw 'A package must contain one root README, license, and manifest.'
        }

        $licenseStream = $licenses[0].Open()
        $licenseBuffer = New-Object IO.MemoryStream
        try {
            $licenseStream.CopyTo($licenseBuffer)
            if ([Convert]::ToBase64String($licenseBuffer.ToArray()) -cne
                [Convert]::ToBase64String($expectedLicenseBytes)) {
                throw 'A package license does not match the repository license.'
            }
        }
        finally {
            $licenseBuffer.Dispose()
            $licenseStream.Dispose()
        }

        $readmeStream = $readmes[0].Open()
        $readmeBuffer = New-Object IO.MemoryStream
        try {
            $readmeStream.CopyTo($readmeBuffer)
            if ([Convert]::ToBase64String($readmeBuffer.ToArray()) -cne
                [Convert]::ToBase64String($expectedReadmeBytes)) {
                throw 'A package README does not match the repository package README.'
            }
        }
        finally {
            $readmeBuffer.Dispose()
            $readmeStream.Dispose()
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
        $descriptionNode = $metadata.SelectSingleNode(
            "*[local-name()='description']")
        $licenseNode = $metadata.SelectSingleNode("*[local-name()='license']")
        $projectUrlNode = $metadata.SelectSingleNode(
            "*[local-name()='projectUrl']")
        $tagsNode = $metadata.SelectSingleNode("*[local-name()='tags']")
        $repositoryNode = $metadata.SelectSingleNode(
            "*[local-name()='repository']")
        if ($null -eq $idNode -or $idNode.InnerText -ne $expectedId -or
            $null -eq $versionNode -or
            $versionNode.InnerText -ne $ExpectedVersion -or
            $null -eq $readmeNode -or
            $readmeNode.InnerText -ne 'README.md' -or
            $null -eq $descriptionNode -or
            [string]::IsNullOrWhiteSpace($descriptionNode.InnerText) -or
            $null -eq $licenseNode -or
            $licenseNode.GetAttribute('type') -ne 'expression' -or
            $licenseNode.InnerText -ne 'Apache-2.0' -or
            $null -eq $projectUrlNode -or
            $projectUrlNode.InnerText -ne
              'https://github.com/EricSun0218/OpenGameAgent' -or
            $null -eq $tagsNode -or
            $tagsNode.InnerText -notmatch '(^|\s)game-ai(\s|$)' -or
            $tagsNode.InnerText -notmatch '(^|\s)agent-runtime(\s|$)' -or
            $null -eq $repositoryNode -or
            $repositoryNode.GetAttribute('type') -ne 'git' -or
            $repositoryNode.GetAttribute('url') -ne
              'https://github.com/EricSun0218/OpenGameAgent.git' -or
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
