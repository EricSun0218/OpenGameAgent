[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$scanner = Join-Path $PSScriptRoot 'Test-TrackedSourcePrivacy.ps1'
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) ('game-agent-tree-self-test-' + [guid]::NewGuid().ToString('N'))
$repository = Join-Path $temporaryRoot 'repository'
$marker = 'blocked-tree-marker'

function Invoke-Git {
    param([Parameter(Mandatory)][string[]]$Arguments)

    & git -C $script:repository @Arguments
    if ($LASTEXITCODE -ne 0) {
        throw 'The tracked source privacy self-test could not prepare Git data.'
    }
}

function Current-Revision {
    $revision = (& git -C $script:repository rev-parse HEAD).Trim()
    if ($LASTEXITCODE -ne 0 -or $revision -notmatch '^[0-9a-f]{40}$') {
        throw 'The tracked source privacy self-test could not resolve a revision.'
    }
    return $revision
}

$null = New-Item -ItemType Directory -Path $repository -Force
try {
    Invoke-Git -Arguments @('init', '--quiet')
    Invoke-Git -Arguments @('config', 'user.name', 'Release Gate Test')
    Invoke-Git -Arguments @(
        'config',
        'user.email',
        'release-gate@example.invalid')

    $privateDirectory = Join-Path $repository 'private-dir'
    $null = New-Item -ItemType Directory -Path $privateDirectory
    [IO.File]::WriteAllText(
        (Join-Path $repository '.gitattributes'),
        "hidden.txt export-ignore`nprivate-dir export-ignore`n",
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $repository 'visible.txt'),
        'safe content',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $repository 'hidden.txt'),
        $marker,
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $privateDirectory ($marker + '.txt')),
        'safe content',
        [Text.UTF8Encoding]::new($false))
    Invoke-Git -Arguments @('add', '--all')
    Invoke-Git -Arguments @(
        'commit',
        '--quiet',
        '-m',
        'privacy scanner bypass fixture')
    $unsafeRevision = Current-Revision

    $archivePath = Join-Path $temporaryRoot 'attribute-filtered.zip'
    Invoke-Git -Arguments @(
        'archive',
        '--format=zip',
        ('--output=' + $archivePath),
        $unsafeRevision)
    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $archivedNames = @($archive.Entries | ForEach-Object FullName)
        $privateEntry = 'private-dir/' + $marker + '.txt'
        if (($archivedNames -contains 'hidden.txt') -or
            ($archivedNames -contains $privateEntry)) {
            throw 'The self-test fixture was not excluded by Git attributes.'
        }
    }
    finally {
        $archive.Dispose()
    }

    $previousPattern = [Environment]::GetEnvironmentVariable(
        'GAME_AGENT_RELEASE_DENY_REGEX')
    try {
        [Environment]::SetEnvironmentVariable(
            'GAME_AGENT_RELEASE_DENY_REGEX',
            $marker)
        $rejected = $false
        try {
            $null = & $scanner `
                -Repository $repository `
                -Revision $unsafeRevision `
                -RequireInjectedDenyRegex
        }
        catch {
            $rejected = $true
            if ($_.Exception.Message.IndexOf(
                    $marker,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw 'The tracked source scanner echoed denied data.'
            }
        }
        if (-not $rejected) {
            throw 'Git attributes hid denied tracked source from the scanner.'
        }

        [IO.File]::WriteAllText(
            (Join-Path $repository 'hidden.txt'),
            'safe hidden content',
            [Text.UTF8Encoding]::new($false))
        [IO.File]::Delete(
            (Join-Path $privateDirectory ($marker + '.txt')))
        [IO.File]::WriteAllText(
            (Join-Path $privateDirectory 'safe.txt'),
            'safe content',
            [Text.UTF8Encoding]::new($false))
        Invoke-Git -Arguments @('add', '--all')
        Invoke-Git -Arguments @(
            'commit',
            '--quiet',
            '-m',
            'safe tracked source fixture')
        $safeRevision = Current-Revision
        $output = @(
            & $scanner `
                -Repository $repository `
                -Revision $safeRevision `
                -RequireInjectedDenyRegex
        )
        if ($output -notcontains 'TRACKED_SOURCE_PRIVACY_PASS') {
            throw 'The tracked source scanner rejected safe source.'
        }

        $archiveFixture = Join-Path $temporaryRoot 'archive-fixture'
        $null = New-Item -ItemType Directory -Path $archiveFixture
        [IO.File]::WriteAllText(
            (Join-Path $archiveFixture 'payload.opaque'),
            $marker,
            [Text.UTF8Encoding]::new($false))
        $opaqueArchive = Join-Path $repository 'opaque.bin'
        [IO.Compression.ZipFile]::CreateFromDirectory(
            $archiveFixture,
            $opaqueArchive)
        Invoke-Git -Arguments @('add', '--all')
        Invoke-Git -Arguments @(
            'commit',
            '--quiet',
            '-m',
            'opaque archive bypass fixture')
        $archiveRejected = $false
        try {
            $null = & $scanner `
                -Repository $repository `
                -Revision (Current-Revision) `
                -RequireInjectedDenyRegex
        }
        catch {
            $archiveRejected = $true
            if ($_.Exception.Message.IndexOf(
                    $marker,
                    [StringComparison]::OrdinalIgnoreCase) -ge 0) {
                throw 'The tracked source scanner echoed denied archive data.'
            }
        }
        if (-not $archiveRejected) {
            throw 'The tracked source scanner accepted denied data in an opaque archive.'
        }

        [IO.File]::Delete($opaqueArchive)
        Invoke-Git -Arguments @('add', '--all')
        Invoke-Git -Arguments @(
            'commit',
            '--quiet',
            '-m',
            'remove opaque archive fixture')

        $oversized = Join-Path $repository 'oversized.bin'
        [IO.File]::WriteAllBytes($oversized, [byte[]](0..16))
        Invoke-Git -Arguments @('add', '--all')
        Invoke-Git -Arguments @(
            'commit',
            '--quiet',
            '-m',
            'bounded blob fixture')
        $oversizedRejected = $false
        try {
            $null = & $scanner `
                -Repository $repository `
                -Revision (Current-Revision) `
                -MaximumBlobBytes 16 `
                -MaximumTotalBytes 1024 `
                -RequireInjectedDenyRegex
        }
        catch {
            $oversizedRejected = $true
        }
        if (-not $oversizedRejected) {
            throw 'The tracked source scanner accepted an oversized blob.'
        }
    }
    finally {
        [Environment]::SetEnvironmentVariable(
            'GAME_AGENT_RELEASE_DENY_REGEX',
            $previousPattern)
    }

    Write-Output 'TRACKED_SOURCE_PRIVACY_SELF_TEST_PASS'
}
finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $systemTemporaryRoot = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
    $requiredPrefix = $systemTemporaryRoot +
        [IO.Path]::DirectorySeparatorChar +
        'game-agent-tree-self-test-'
    if ($resolvedTemporaryRoot.StartsWith(
            $requiredPrefix,
            [StringComparison]::Ordinal) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
