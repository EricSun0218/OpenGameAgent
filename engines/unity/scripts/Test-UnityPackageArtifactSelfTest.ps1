[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$ArtifactPath,

    [switch]$ExpectSymbols
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$checker = Join-Path $PSScriptRoot 'Test-UnityPackageArtifact.ps1'
$sourceArtifact = Get-Item -LiteralPath (
    Resolve-Path -LiteralPath $ArtifactPath)
if (-not $sourceArtifact.PSIsContainer -or
    ($sourceArtifact.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The Unity package self-test requires a regular directory.'
}
$manifest = Get-Content -LiteralPath (
    Join-Path $sourceArtifact.FullName 'package.json') -Raw |
    ConvertFrom-Json
$expectedVersion = [string]$manifest.version
if ([string]::IsNullOrWhiteSpace($expectedVersion)) {
    throw 'The Unity package self-test requires a versioned package.'
}

$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) ('game-agent-unity-package-self-test-' + [guid]::NewGuid().ToString('N'))

function Copy-SourceArtifact {
    param(
        [Parameter(Mandatory)]
        [string]$Destination
    )

    $null = New-Item -ItemType Directory -Path $Destination
    foreach ($child in Get-ChildItem -LiteralPath (
            $script:sourceArtifact.FullName) -Force) {
        Copy-Item `
            -LiteralPath $child.FullName `
            -Destination $Destination `
            -Recurse `
            -Force
    }
}

function New-MutatedArtifact {
    param(
        [Parameter(Mandatory)]
        [string]$Destination,

        [Parameter(Mandatory)]
        [string]$RelativePath
    )

    Copy-SourceArtifact -Destination $Destination
    $nativeRelativePath = $RelativePath.Replace(
        '/',
        [IO.Path]::DirectorySeparatorChar)
    $roguePath = Join-Path $Destination $nativeRelativePath
    $parent = Split-Path -Parent $roguePath
    if (-not (Test-Path -LiteralPath $parent -PathType Container)) {
        $null = New-Item -ItemType Directory -Path $parent -Force
    }
    $writePath = [IO.Path]::GetFullPath($Destination).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)) +
        [IO.Path]::DirectorySeparatorChar +
        $nativeRelativePath
    if ([Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        if ($writePath.StartsWith(
                '\\',
                [StringComparison]::Ordinal)) {
            $writePath = '\\?\UNC\' + $writePath.TrimStart('\')
        }
        else {
            $writePath = '\\?\' + $writePath
        }
    }
    [IO.File]::WriteAllBytes($writePath, [byte[]]@(0x2a))
}

function Invoke-CheckerExpectingRejection {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$Fixture,

        [Parameter(Mandatory)]
        [string]$ExpectedMessage
    )

    $checkerParameters = @{
        ArtifactPath = $Fixture
        ExpectedVersion = $script:expectedVersion
        WorkingPath = Join-Path $script:temporaryRoot ($Name + '-consumer')
    }
    if ($script:ExpectSymbols) {
        $checkerParameters.ExpectSymbols = $true
    }

    $rejected = $false
    try {
        $null = & $script:checker @checkerParameters
    }
    catch {
        if ($_.Exception.Message.IndexOf(
                $ExpectedMessage,
                [StringComparison]::Ordinal) -lt 0) {
            throw "Unity package self-test '$Name' failed for the wrong reason: $($_.Exception.Message)"
        }
        $rejected = $true
    }

    if (-not $rejected) {
        throw "The Unity package verifier accepted '$Name'."
    }
}

function Assert-MutatedArtifactRejected {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [string]$ExpectedMessage
    )

    $fixture = Join-Path $script:temporaryRoot $Name
    New-MutatedArtifact `
        -Destination $fixture `
        -RelativePath $RelativePath

    Invoke-CheckerExpectingRejection `
        -Name $Name `
        -Fixture $fixture `
        -ExpectedMessage $ExpectedMessage
}

function Assert-CopiedArtifactMutationRejected {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$ExpectedMessage,

        [Parameter(Mandatory)]
        [scriptblock]$Mutation
    )

    $fixture = Join-Path $script:temporaryRoot $Name
    Copy-SourceArtifact -Destination $fixture
    & $Mutation $fixture
    Invoke-CheckerExpectingRejection `
        -Name $Name `
        -Fixture $fixture `
        -ExpectedMessage $ExpectedMessage
}

$null = New-Item -ItemType Directory -Path $temporaryRoot
try {
    $cases = @(
        @{
            Name = 'relocated-managed-dll'
            Path = 'Documentation~/GameAgent.Runtime.dll'
            Message = 'global DLL set is invalid'
        },
        @{
            Name = 'uppercase-extra-dll'
            Path = 'Documentation~/rogue.DLL'
            Message = 'global DLL set is invalid'
        },
        @{
            Name = 'relocated-managed-pdb'
            Path = 'Documentation~/GameAgent.Runtime.pdb'
            Message = 'global PDB set is invalid'
        },
        @{
            Name = 'native-executable'
            Path = 'Documentation~/rogue.exe'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'native-so'
            Path = 'Documentation~/rogue.so'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'versioned-native-so'
            Path = 'Documentation~/rogue.so.1'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'native-dylib'
            Path = 'Documentation~/rogue.dylib'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'native-bundle'
            Path = 'Documentation~/rogue.bundle'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'trailing-dot-dll'
            Path = 'Documentation~/rogue.dll.'
            Message = 'non-portable Windows path'
        },
        @{
            Name = 'trailing-space-executable'
            Path = 'Documentation~/rogue.EXE '
            Message = 'non-portable Windows path'
        },
        @{
            Name = 'trailing-dot-versioned-so'
            Path = 'Documentation~/rogue.so.1.'
            Message = 'non-portable Windows path'
        },
        @{
            Name = 'reserved-device-name'
            Path = 'Documentation~/CON.dll'
            Message = 'non-portable Windows path'
        },
        @{
            Name = 'orphan-dll-metadata'
            Path = 'Documentation~/rogue.dll.meta'
            Message = 'global DLL metadata set is invalid'
        },
        @{
            Name = 'orphan-pdb-metadata'
            Path = 'Documentation~/rogue.pdb.meta'
            Message = 'global PDB metadata set is invalid'
        })
    if ([Environment]::OSVersion.Platform -ne [PlatformID]::Win32NT) {
        $cases += @{
            Name = 'alternate-data-stream-name'
            Path = 'Documentation~/rogue.dll:payload'
            Message = 'non-portable Windows path'
        }
    }

    foreach ($case in $cases) {
        Assert-MutatedArtifactRejected `
            -Name $case.Name `
            -RelativePath $case.Path `
            -ExpectedMessage $case.Message
    }

    $coreMetaRelativePath =
        'Runtime/Plugins/GameAgent.Core.dll.meta'
    Assert-CopiedArtifactMutationRejected `
        -Name 'missing-dll-metadata' `
        -ExpectedMessage 'global DLL metadata set is invalid' `
        -Mutation {
            param($fixture)
            Remove-Item -LiteralPath (
                Join-Path $fixture $coreMetaRelativePath)
        }
    Assert-CopiedArtifactMutationRejected `
        -Name 'relocated-dll-metadata' `
        -ExpectedMessage 'global DLL metadata set is invalid' `
        -Mutation {
            param($fixture)
            $destination = Join-Path $fixture (
                'Documentation~' +
                [IO.Path]::DirectorySeparatorChar +
                'GameAgent.Core.dll.meta')
            Move-Item `
                -LiteralPath (Join-Path $fixture $coreMetaRelativePath) `
                -Destination $destination
        }
    Assert-CopiedArtifactMutationRejected `
        -Name 'disabled-dll-plugin-importer' `
        -ExpectedMessage 'PluginImporter metadata is invalid' `
        -Mutation {
            param($fixture)
            $path = Join-Path $fixture $coreMetaRelativePath
            $content = [IO.File]::ReadAllText($path)
            $mutated = $content.Replace(
                '      enabled: 1',
                '      enabled: 0')
            if ($mutated -ceq $content) {
                throw 'The Unity metadata fixture could not be disabled.'
            }
            [IO.File]::WriteAllText(
                $path,
                $mutated,
                [Text.UTF8Encoding]::new($false))
        }
    Assert-CopiedArtifactMutationRejected `
        -Name 'tampered-dll-guid' `
        -ExpectedMessage 'PluginImporter metadata is invalid' `
        -Mutation {
            param($fixture)
            $path = Join-Path $fixture $coreMetaRelativePath
            $content = [IO.File]::ReadAllText($path)
            $mutated = [regex]::Replace(
                $content,
                '(?m)^guid: [0-9a-f]{32}$',
                'guid: 00000000000000000000000000000000')
            if ($mutated -ceq $content) {
                throw 'The Unity metadata GUID fixture could not be changed.'
            }
            [IO.File]::WriteAllText(
                $path,
                $mutated,
                [Text.UTF8Encoding]::new($false))
        }
    Assert-CopiedArtifactMutationRejected `
        -Name 'missing-assembly-definition-dependency' `
        -ExpectedMessage 'assembly definition dependency set is invalid' `
        -Mutation {
            param($fixture)
            $path = Join-Path $fixture (
                'Runtime' +
                [IO.Path]::DirectorySeparatorChar +
                'GameAgent.Unity.asmdef')
            $definition = Get-Content -LiteralPath $path -Raw |
                ConvertFrom-Json
            $definition.precompiledReferences = @(
                $definition.precompiledReferences |
                    Where-Object { $_ -ne 'GameAgent.Core.dll' })
            $definition | ConvertTo-Json -Depth 10 |
                Set-Content -LiteralPath $path -Encoding UTF8
        }
    Assert-CopiedArtifactMutationRejected `
        -Name 'disabled-assembly-definition-override' `
        -ExpectedMessage 'assembly definition does not override references' `
        -Mutation {
            param($fixture)
            $path = Join-Path $fixture (
                'Tests' +
                [IO.Path]::DirectorySeparatorChar +
                'Runtime' +
                [IO.Path]::DirectorySeparatorChar +
                'GameAgent.Unity.PlayModeTests.asmdef')
            $definition = Get-Content -LiteralPath $path -Raw |
                ConvertFrom-Json
            $definition.overrideReferences = $false
            $definition | ConvertTo-Json -Depth 10 |
                Set-Content -LiteralPath $path -Encoding UTF8
        }

    if ($ExpectSymbols) {
        $symbolMetaRelativePath =
            'Runtime/Plugins/GameAgent.Core.pdb.meta'
        Assert-CopiedArtifactMutationRejected `
            -Name 'missing-pdb-metadata' `
            -ExpectedMessage 'global PDB metadata set is invalid' `
            -Mutation {
                param($fixture)
                Remove-Item -LiteralPath (
                    Join-Path $fixture $symbolMetaRelativePath)
            }
        Assert-CopiedArtifactMutationRejected `
            -Name 'tampered-pdb-plugin-importer' `
            -ExpectedMessage 'PluginImporter metadata is invalid' `
            -Mutation {
                param($fixture)
                $path = Join-Path $fixture $symbolMetaRelativePath
                $content = [IO.File]::ReadAllText($path)
                $mutated = $content.Replace(
                    '  validateReferences: 1',
                    '  validateReferences: 0')
                if ($mutated -ceq $content) {
                    throw 'The Unity symbol metadata fixture could not be changed.'
                }
                [IO.File]::WriteAllText(
                    $path,
                    $mutated,
                    [Text.UTF8Encoding]::new($false))
            }
    }

    Write-Output 'UNITY_PACKAGE_ARTIFACT_SELF_TEST_PASS'
}
finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $systemTemporaryRoot = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
    $requiredPrefix = $systemTemporaryRoot +
        [IO.Path]::DirectorySeparatorChar
    $comparison = if (
        [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    ) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    $isExpectedTemporaryDirectory = (
        $resolvedTemporaryRoot.StartsWith($requiredPrefix, $comparison) -and
        [IO.Path]::GetFileName($resolvedTemporaryRoot).StartsWith(
            'game-agent-unity-package-self-test-',
            [StringComparison]::Ordinal)
    )
    if ($isExpectedTemporaryDirectory -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot -PathType Container)) {
        try {
            Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
        }
        catch {
            if ([Environment]::OSVersion.Platform -ne
                [PlatformID]::Win32NT) {
                throw
            }

            # Windows PowerShell normalizes trailing dots and spaces while
            # removing paths. The self-test intentionally creates those names,
            # so retry the already validated temp root through an extended path.
            $extendedPath = if ($resolvedTemporaryRoot.StartsWith('\\')) {
                '\\?\UNC\' + $resolvedTemporaryRoot.Substring(2)
            }
            else {
                '\\?\' + $resolvedTemporaryRoot
            }
            [IO.Directory]::Delete($extendedPath, $true)
        }
    }
}
