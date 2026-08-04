[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Archive
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$verifier = Join-Path $PSScriptRoot 'verify-packaged-addon.ps1'
$sourceArchive = Get-Item -LiteralPath (
    Resolve-Path -LiteralPath $Archive)
if ($sourceArchive.PSIsContainer) {
    throw 'The Godot package self-test requires an archive file.'
}

$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) ('game-agent-godot-package-self-test-' + [guid]::NewGuid().ToString('N'))

function New-MutatedArchive {
    param(
        [Parameter(Mandatory)]
        [string]$Destination,

        [Parameter(Mandatory)]
        [string]$EntryName
    )

    Copy-Item -LiteralPath $script:sourceArchive.FullName -Destination $Destination
    $archiveHandle = [IO.Compression.ZipFile]::Open(
        $Destination,
        [IO.Compression.ZipArchiveMode]::Update)
    try {
        if ($null -ne $archiveHandle.GetEntry($EntryName)) {
            throw "The self-test entry already exists: '$EntryName'."
        }

        $entry = $archiveHandle.CreateEntry($EntryName)
        $stream = $entry.Open()
        try {
            $stream.WriteByte(0x2a)
        }
        finally {
            $stream.Dispose()
        }
    }
    finally {
        $archiveHandle.Dispose()
    }
}

function Assert-MutatedArchiveRejected {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$EntryName,

        [Parameter(Mandatory)]
        [string]$ExpectedMessage
    )

    $fixtureRoot = Join-Path $script:temporaryRoot $Name
    $null = New-Item -ItemType Directory -Path $fixtureRoot
    $fixture = Join-Path $fixtureRoot $script:sourceArchive.Name
    New-MutatedArchive -Destination $fixture -EntryName $EntryName

    $rejected = $false
    try {
        $null = & $script:verifier `
            -Archive $fixture `
            -Godot '__unused_for_structurally_rejected_package__'
    }
    catch {
        if ($_.Exception.Message.IndexOf(
                $ExpectedMessage,
                [StringComparison]::Ordinal) -lt 0) {
            throw "Godot package self-test '$Name' failed for the wrong reason: $($_.Exception.Message)"
        }
        $rejected = $true
    }

    if (-not $rejected) {
        throw "The Godot package verifier accepted '$Name'."
    }
}

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem
$null = New-Item -ItemType Directory -Path $temporaryRoot
try {
    $cases = @(
        @{
            Name = 'relocated-managed-dll'
            Entry = 'addons/game_agent_runtime/docs/GameAgent.Runtime.dll'
            Message = 'managed-library set is incomplete or unexpected'
        },
        @{
            Name = 'uppercase-extra-dll'
            Entry = 'addons/game_agent_runtime/docs/rogue.DLL'
            Message = 'managed-library set is incomplete or unexpected'
        },
        @{
            Name = 'relocated-managed-pdb'
            Entry = 'addons/game_agent_runtime/docs/GameAgent.Runtime.pdb'
            Message = 'managed-symbol set is unexpected'
        },
        @{
            Name = 'native-executable'
            Entry = 'addons/game_agent_runtime/docs/rogue.exe'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'native-so'
            Entry = 'addons/game_agent_runtime/docs/rogue.so'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'versioned-native-so'
            Entry = 'addons/game_agent_runtime/docs/rogue.so.1'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'native-dylib'
            Entry = 'addons/game_agent_runtime/docs/rogue.dylib'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'native-bundle'
            Entry = 'addons/game_agent_runtime/docs/rogue.bundle'
            Message = 'unsupported executable binary'
        },
        @{
            Name = 'trailing-dot-dll'
            Entry = 'addons/game_agent_runtime/docs/rogue.dll.'
            Message = 'non-canonical entry name'
        },
        @{
            Name = 'alternate-data-stream'
            Entry = 'addons/game_agent_runtime/docs/rogue.dll:payload'
            Message = 'non-canonical entry name'
        },
        @{
            Name = 'trailing-space-executable'
            Entry = 'addons/game_agent_runtime/docs/rogue.EXE '
            Message = 'non-canonical entry name'
        },
        @{
            Name = 'trailing-dot-versioned-so'
            Entry = 'addons/game_agent_runtime/docs/rogue.so.1.'
            Message = 'non-canonical entry name'
        },
        @{
            Name = 'reserved-device-name'
            Entry = 'addons/game_agent_runtime/docs/CON.dll'
            Message = 'non-canonical entry name'
        })

    foreach ($case in $cases) {
        Assert-MutatedArchiveRejected `
            -Name $case.Name `
            -EntryName $case.Entry `
            -ExpectedMessage $case.Message
    }

    $versionRejected = $false
    try {
        $null = & $verifier `
            -Archive $sourceArchive.FullName `
            -Godot '__unused_for_version_rejected_package__' `
            -ExpectedVersion '9.9.9-test'
    }
    catch {
        if ($_.Exception.Message.IndexOf(
                'does not match the expected release version',
                [StringComparison]::Ordinal) -lt 0) {
            throw "Godot package expected-version self-test failed for the wrong reason: $($_.Exception.Message)"
        }
        $versionRejected = $true
    }
    if (-not $versionRejected) {
        throw 'The Godot package verifier accepted the wrong expected version.'
    }

    Write-Output 'GODOT_PACKAGE_ARTIFACT_SELF_TEST_PASS'
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
            'game-agent-godot-package-self-test-',
            [StringComparison]::Ordinal)
    )
    if ($isExpectedTemporaryDirectory -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot -PathType Container)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
