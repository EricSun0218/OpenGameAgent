$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$root = Join-Path ([IO.Path]::GetTempPath()) (
    'game-agent-directory-archive-' + [Guid]::NewGuid().ToString('N'))
$source = Join-Path $root 'source'
$first = Join-Path $root 'first.zip'
$second = Join-Path $root 'second.zip'

try {
    [IO.Directory]::CreateDirectory((Join-Path $source 'nested')) | Out-Null
    [IO.File]::WriteAllText(
        (Join-Path $source 'package.json'),
        '{"name":"test"}',
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $source 'nested\value.txt'),
        "stable`n",
        [Text.UTF8Encoding]::new($false))

    & (Join-Path $PSScriptRoot 'Write-DeterministicDirectoryArchive.ps1') `
        -SourcePath $source `
        -DestinationPath $first `
        -RootDirectoryName 'package' | Out-Null
    Get-ChildItem -LiteralPath $source -File -Recurse | ForEach-Object {
        $_.LastWriteTimeUtc = [DateTime]::UtcNow.AddDays(5)
    }
    & (Join-Path $PSScriptRoot 'Write-DeterministicDirectoryArchive.ps1') `
        -SourcePath $source `
        -DestinationPath $second `
        -RootDirectoryName 'package' | Out-Null

    $firstHash = (Get-FileHash -LiteralPath $first -Algorithm SHA256).Hash
    $secondHash = (Get-FileHash -LiteralPath $second -Algorithm SHA256).Hash
    if ($firstHash -cne $secondHash) {
        throw 'Canonical archives differ after source timestamp changes.'
    }

    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($first)
    try {
        $entries = @($archive.Entries.FullName | Sort-Object -CaseSensitive)
        $expected = @('package/nested/value.txt', 'package/package.json')
        if ([string]::Join("`n", $entries) -cne [string]::Join("`n", $expected)) {
            throw 'The canonical archive manifest is invalid.'
        }
        if (@($archive.Entries | Where-Object {
                    $_.LastWriteTime.DateTime -ne
                        [DateTime]::new(1980, 1, 1, 0, 0, 0, [DateTimeKind]::Unspecified)
                }).Count -ne 0) {
            throw 'The canonical archive contains a non-canonical timestamp.'
        }
    }
    finally {
        $archive.Dispose()
    }

    $unsafeRejected = $false
    try {
        & (Join-Path $PSScriptRoot 'Write-DeterministicDirectoryArchive.ps1') `
            -SourcePath $source `
            -DestinationPath (Join-Path $root 'unsafe.zip') `
            -RootDirectoryName '..'
    }
    catch {
        $unsafeRejected = $true
    }
    if (-not $unsafeRejected) {
        throw 'An unsafe archive root name was accepted.'
    }
}
finally {
    if (Test-Path -LiteralPath $root -PathType Container) {
        Remove-Item -LiteralPath $root -Recurse -Force
    }
}

Write-Output 'Deterministic directory archive self-test passed.'
