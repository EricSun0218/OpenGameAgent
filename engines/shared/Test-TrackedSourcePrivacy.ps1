[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Repository,

    [Parameter(Mandatory)]
    [string]$Revision,

    [switch]$RequireInjectedDenyRegex,

    [long]$MaximumBlobBytes = 16777216L,

    [long]$MaximumTotalBytes = 134217728L,

    [int]$MaximumEntries = 10000
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

if ($Revision -notmatch '^[0-9a-fA-F]{40}$') {
    throw 'The tracked source revision must be a complete commit id.'
}
if ($MaximumBlobBytes -lt 1 -or
    $MaximumTotalBytes -lt $MaximumBlobBytes -or
    $MaximumEntries -lt 1) {
    throw 'The tracked source scan limits are invalid.'
}

$repositoryPath = [IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath $Repository))
$scanner = Join-Path $PSScriptRoot 'Test-ReleaseArtifactPrivacy.ps1'
$injectedDenyRegex = [Environment]::GetEnvironmentVariable(
    'GAME_AGENT_RELEASE_DENY_REGEX')
if ($RequireInjectedDenyRegex -and
    [string]::IsNullOrWhiteSpace($injectedDenyRegex)) {
    throw 'The required release deny configuration is unavailable.'
}
$injectedDenyPatterns = @(
    if (-not [string]::IsNullOrWhiteSpace($injectedDenyRegex)) {
        $injectedDenyRegex -split '\r?\n' |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) }
    }
)
$temporaryBase = [IO.Path]::GetFullPath([IO.Path]::GetTempPath()).TrimEnd(
    [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar))
$temporaryRoot = Join-Path (
    $temporaryBase
) ('game-agent-tree-scan-' + [guid]::NewGuid().ToString('N'))
$manifestPath = Join-Path $temporaryRoot 'tree-manifest.bin'
$blobRoot = Join-Path $temporaryRoot 'blobs'

function Invoke-GitToFile {
    param(
        [Parameter(Mandatory)]
        [string[]]$Arguments,

        [Parameter(Mandatory)]
        [string]$OutputPath,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    function ConvertTo-NativeArgument {
        param([Parameter(Mandatory)][AllowEmptyString()][string]$Value)

        if ($Value.Length -gt 0 -and $Value -notmatch '[\s"]') {
            return $Value
        }

        $builder = New-Object Text.StringBuilder
        $null = $builder.Append('"')
        $backslashes = 0
        foreach ($character in $Value.ToCharArray()) {
            if ($character -eq '\') {
                $backslashes++
                continue
            }
            if ($character -eq '"') {
                $null = $builder.Append(('\' * ($backslashes * 2 + 1)))
                $null = $builder.Append('"')
                $backslashes = 0
                continue
            }

            if ($backslashes -gt 0) {
                $null = $builder.Append(('\' * $backslashes))
                $backslashes = 0
            }
            $null = $builder.Append($character)
        }
        if ($backslashes -gt 0) {
            $null = $builder.Append(('\' * ($backslashes * 2)))
        }
        $null = $builder.Append('"')
        return $builder.ToString()
    }

    $start = New-Object Diagnostics.ProcessStartInfo
    $start.FileName = 'git'
    $start.UseShellExecute = $false
    $start.CreateNoWindow = $true
    $start.RedirectStandardOutput = $true
    $start.RedirectStandardError = $true
    if ($null -ne $start.PSObject.Properties['Environment']) {
        $null = $start.Environment.Remove(
            'GAME_AGENT_RELEASE_DENY_REGEX')
    }
    else {
        $start.EnvironmentVariables.Remove(
            'GAME_AGENT_RELEASE_DENY_REGEX')
    }
    if ($null -ne $start.PSObject.Properties['ArgumentList']) {
        foreach ($argument in $Arguments) {
            $start.ArgumentList.Add($argument)
        }
    }
    else {
        $start.Arguments = (
            $Arguments |
                ForEach-Object { ConvertTo-NativeArgument -Value $_ }
        ) -join ' '
    }

    $process = New-Object Diagnostics.Process
    $process.StartInfo = $start
    $output = $null
    try {
        if (-not $process.Start()) {
            throw $FailureMessage
        }
        $errorRead = $process.StandardError.ReadToEndAsync()
        $output = [IO.File]::Open(
            $OutputPath,
            [IO.FileMode]::Create,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $process.StandardOutput.BaseStream.CopyTo($output)
        $output.Dispose()
        $output = $null
        $process.WaitForExit()
        $null = $errorRead.GetAwaiter().GetResult()
        if ($process.ExitCode -ne 0) {
            throw $FailureMessage
        }
    }
    finally {
        if ($null -ne $output) {
            $output.Dispose()
        }
        $process.Dispose()
    }
}

function Read-AsciiFile {
    param([Parameter(Mandatory)][string]$Path)

    return [Text.Encoding]::ASCII.GetString(
        [IO.File]::ReadAllBytes($Path)).Trim()
}

$null = New-Item -ItemType Directory -Path $blobRoot -Force
try {
    $revisionOutput = Join-Path $temporaryRoot 'revision.txt'
    Invoke-GitToFile `
        -Arguments @(
            '-C',
            $repositoryPath,
            'rev-parse',
            '--verify',
            ($Revision + '^{commit}')) `
        -OutputPath $revisionOutput `
        -FailureMessage 'The tracked source revision could not be resolved.'
    $resolvedRevision = Read-AsciiFile -Path $revisionOutput
    if (-not [string]::Equals(
            $resolvedRevision,
            $Revision,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The resolved tracked source revision does not match the request.'
    }

    Invoke-GitToFile `
        -Arguments @(
            '-C',
            $repositoryPath,
            'ls-tree',
            '-r',
            '-z',
            '--full-tree',
            $resolvedRevision) `
        -OutputPath $manifestPath `
        -FailureMessage 'The tracked source tree could not be enumerated.'

    $manifest = [IO.File]::ReadAllBytes($manifestPath)
    $offset = 0
    $entryCount = 0
    $blobIds = New-Object 'Collections.Generic.HashSet[string]' (
        [StringComparer]::OrdinalIgnoreCase)
    $entryPattern =
        '^(?<mode>100644|100755|120000) blob (?<oid>[0-9a-f]{40})$'
    while ($offset -lt $manifest.Length) {
        $recordEnd = $offset
        while ($recordEnd -lt $manifest.Length -and
               $manifest[$recordEnd] -ne 0) {
            $recordEnd++
        }
        if ($recordEnd -ge $manifest.Length) {
            throw 'The tracked source tree manifest is malformed.'
        }

        $tab = $offset
        while ($tab -lt $recordEnd -and $manifest[$tab] -ne 9) {
            $tab++
        }
        if ($tab -eq $recordEnd) {
            throw 'The tracked source tree manifest is malformed.'
        }

        $header = [Text.Encoding]::ASCII.GetString(
            $manifest,
            $offset,
            $tab - $offset)
        if ($header -notmatch $entryPattern) {
            throw 'The tracked source tree contains an unsupported entry.'
        }

        $pathBytes = $recordEnd - $tab - 1
        if ($pathBytes -lt 1 -or $pathBytes -gt 4096) {
            throw 'A tracked source path exceeds the scanner limit.'
        }

        $entryCount++
        if ($entryCount -gt $MaximumEntries) {
            throw 'The tracked source tree exceeds the entry limit.'
        }
        $null = $blobIds.Add($Matches.oid)
        $offset = $recordEnd + 1
    }

    $totalBytes = [long]$manifest.Length
    $blobIndex = 0
    foreach ($blobId in ($blobIds | Sort-Object)) {
        $sizeOutput = Join-Path $temporaryRoot (
            'blob-size-' + $blobIndex.ToString('D8') + '.txt')
        Invoke-GitToFile `
            -Arguments @(
                '-C',
                $repositoryPath,
                'cat-file',
                '-s',
                $blobId) `
            -OutputPath $sizeOutput `
            -FailureMessage 'A tracked source blob size could not be read.'
        $blobSize = 0L
        if (-not [long]::TryParse(
                (Read-AsciiFile -Path $sizeOutput),
                [Globalization.NumberStyles]::None,
                [Globalization.CultureInfo]::InvariantCulture,
                [ref]$blobSize) -or
            $blobSize -lt 0 -or
            $blobSize -gt $MaximumBlobBytes) {
            throw 'A tracked source blob exceeds the scanner limit.'
        }

        if ($blobSize -gt $MaximumTotalBytes - $totalBytes) {
            throw 'The tracked source tree exceeds the total scanner limit.'
        }
        $totalBytes += $blobSize

        $blobPath = Join-Path $blobRoot (
            $blobIndex.ToString('D8') + '-' + $blobId + '.bin')
        Invoke-GitToFile `
            -Arguments @(
                '-C',
                $repositoryPath,
                'cat-file',
                'blob',
                $blobId) `
            -OutputPath $blobPath `
            -FailureMessage 'A tracked source blob could not be read.'
        if ((Get-Item -LiteralPath $blobPath).Length -ne $blobSize) {
            throw 'A tracked source blob changed while it was read.'
        }
        $blobIndex++
    }

    if ($injectedDenyPatterns.Count -gt 0) {
        & $scanner `
            -Path @($manifestPath, $blobRoot) `
            -DeniedRegex $injectedDenyPatterns
    }
    else {
        & $scanner -Path @($manifestPath, $blobRoot)
    }
    Write-Output 'TRACKED_SOURCE_PRIVACY_PASS'
}
finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $requiredPrefix = $temporaryBase +
        [IO.Path]::DirectorySeparatorChar +
        'game-agent-tree-scan-'
    if ($resolvedTemporaryRoot.StartsWith(
            $requiredPrefix,
            [StringComparison]::Ordinal) -and
        (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
