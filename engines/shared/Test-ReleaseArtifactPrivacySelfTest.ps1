[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scanner = Join-Path $PSScriptRoot 'Test-ReleaseArtifactPrivacy.ps1'
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) ('game-agent-release-privacy-' + [guid]::NewGuid().ToString('N'))
$deniedMarker = 'blocked-release-marker'
$credentialValue = 's' + 'k-' + ('a' * 32)
$genericCredential = 'MODEL' + '_API' + '_KEY=' + ('b' * 32)
$jsonCredential = '"' + 'CLIENT' + '_SECRET": "' + ('c' * 32) + '"'

function Invoke-ScannerPass {
    param(
        [Parameter(Mandatory)]
        [string]$Target,

        [string[]]$DeniedRegex = @()
    )

    $output = @(
        & $script:scanner -Path $Target -DeniedRegex $DeniedRegex
    )
    if ($output -notcontains 'RELEASE_ARTIFACT_PRIVACY_PASS') {
        throw 'The release privacy scanner did not emit its pass marker.'
    }
}

function Invoke-ScannerReject {
    param(
        [Parameter(Mandatory)]
        [string]$Target,

        [string[]]$DeniedRegex = @(),

        [string[]]$MustNotEcho = @()
    )

    $rejected = $false
    try {
        $null = & $script:scanner `
            -Path $Target `
            -DeniedRegex $DeniedRegex
    }
    catch {
        $rejected = $true
        foreach ($privateValue in $MustNotEcho) {
            if (-not [string]::IsNullOrEmpty($privateValue) -and
                $_.Exception.Message.IndexOf(
                    $privateValue,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw 'The privacy scanner echoed rejected data in its error.'
            }
        }
    }
    if (-not $rejected) {
        throw 'The release privacy scanner accepted unsafe test data.'
    }
}

function New-TestArchive {
    param(
        [Parameter(Mandatory)]
        [string]$ArchivePath,

        [Parameter(Mandatory)]
        [hashtable]$Entries
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::Open(
        $ArchivePath,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        foreach ($entryName in $Entries.Keys) {
            $entry = $archive.CreateEntry($entryName)
            $stream = $entry.Open()
            $writer = New-Object IO.StreamWriter(
                $stream,
                [Text.UTF8Encoding]::new($false))
            try {
                $writer.Write([string]$Entries[$entryName])
            }
            finally {
                $writer.Dispose()
                $stream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$null = New-Item -ItemType Directory -Path $temporaryRoot -Force
try {
    $safeDirectory = Join-Path $temporaryRoot 'safe-directory'
    $null = New-Item -ItemType Directory -Path (
        Join-Path $safeDirectory 'nested') -Force
    [IO.File]::WriteAllText(
        (Join-Path $safeDirectory 'nested\payload.meta'),
        'safe release content',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllBytes(
        (Join-Path $safeDirectory 'opaque.bin'),
        [byte[]](0, 1, 2, 3, 254, 255))
    Invoke-ScannerPass -Target $safeDirectory

    $safeArchive = Join-Path $temporaryRoot 'safe.zip'
    New-TestArchive `
        -ArchivePath $safeArchive `
        -Entries @{
            'nested/payload.custom' = 'safe archive content'
            'empty/' = ''
        }
    Invoke-ScannerPass -Target $safeArchive

    $deniedNameDirectory = Join-Path $temporaryRoot 'denied-name'
    $null = New-Item -ItemType Directory -Path $deniedNameDirectory
    [IO.File]::WriteAllText(
        (Join-Path $deniedNameDirectory "$deniedMarker.txt"),
        'safe content',
        [Text.UTF8Encoding]::new($false))
    Invoke-ScannerReject `
        -Target $deniedNameDirectory `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $deniedArchive = Join-Path $temporaryRoot 'denied-name.zip'
    New-TestArchive `
        -ArchivePath $deniedArchive `
        -Entries @{
            "nested/$deniedMarker/" = ''
        }
    Invoke-ScannerReject `
        -Target $deniedArchive `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $credentialDirectory = Join-Path $temporaryRoot 'credential'
    $null = New-Item -ItemType Directory -Path $credentialDirectory
    [IO.File]::WriteAllText(
        (Join-Path $credentialDirectory 'payload.unknown'),
        ($credentialValue +
            [Environment]::NewLine +
            $genericCredential +
            [Environment]::NewLine +
            $jsonCredential),
        [Text.UTF8Encoding]::new($false))
    Invoke-ScannerReject `
        -Target $credentialDirectory `
        -MustNotEcho @(
            $credentialValue,
            $genericCredential,
            $jsonCredential)

    $credentialArchive = Join-Path $temporaryRoot 'credential.zip'
    New-TestArchive `
        -ArchivePath $credentialArchive `
        -Entries @{
            'payload.data' = $credentialValue
        }
    Invoke-ScannerReject `
        -Target $credentialArchive `
        -MustNotEcho $credentialValue

    $pathDirectory = Join-Path $temporaryRoot 'private-path'
    $null = New-Item -ItemType Directory -Path $pathDirectory
    $userProfile = [Environment]::GetFolderPath(
        [Environment+SpecialFolder]::UserProfile)
    $privatePath = if ([string]::Equals(
            $userProfile,
            '/root',
            [StringComparison]::Ordinal)) {
        ('/' + 'root' + '/' + 'work' + '/private-build-input')
    }
    else {
        Join-Path $userProfile 'private-build-input'
    }
    [IO.File]::WriteAllText(
        (Join-Path $pathDirectory 'path.txt'),
        $privatePath,
        [Text.UTF8Encoding]::new($false))
    Invoke-ScannerReject `
        -Target $pathDirectory `
        -MustNotEcho $privatePath

    $previousInjectedPattern = [Environment]::GetEnvironmentVariable(
        'GAME_AGENT_RELEASE_DENY_REGEX')
    try {
        [Environment]::SetEnvironmentVariable(
            'GAME_AGENT_RELEASE_DENY_REGEX',
            $deniedMarker)
        $injectedDirectory = Join-Path $temporaryRoot 'injected-pattern'
        $null = New-Item -ItemType Directory -Path $injectedDirectory
        [IO.File]::WriteAllText(
            (Join-Path $injectedDirectory 'payload.txt'),
            $deniedMarker,
            [Text.UTF8Encoding]::new($false))
        Invoke-ScannerReject `
            -Target $injectedDirectory `
            -MustNotEcho $deniedMarker
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'GAME_AGENT_RELEASE_DENY_REGEX',
            $previousInjectedPattern)
    }

    Write-Output 'RELEASE_ARTIFACT_PRIVACY_SELF_TEST_PASS'
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
    if ($resolvedTemporaryRoot.StartsWith(
            $requiredPrefix,
            [StringComparison]::OrdinalIgnoreCase) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
