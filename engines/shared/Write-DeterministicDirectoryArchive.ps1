[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)][string]$SourcePath,
    [Parameter(Mandatory = $true)][string]$DestinationPath,
    [Parameter(Mandatory = $true)][string]$RootDirectoryName,
    [switch]$Force
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

Add-Type -AssemblyName System.IO.Compression
Add-Type -AssemblyName System.IO.Compression.FileSystem

$source = [IO.Path]::GetFullPath($SourcePath)
$destination = [IO.Path]::GetFullPath($DestinationPath)
if (-not (Test-Path -LiteralPath $source -PathType Container)) {
    throw "The archive source directory does not exist: '$source'."
}
if ($RootDirectoryName -notmatch '^[A-Za-z0-9][A-Za-z0-9._-]{0,127}$' -or
    $RootDirectoryName -in @('.', '..')) {
    throw 'The archive root directory name is invalid.'
}

$sourceItem = Get-Item -LiteralPath $source -Force
if (($sourceItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The archive source directory must not be a filesystem link.'
}

$sourcePrefix = $source.TrimEnd(
    [IO.Path]::DirectorySeparatorChar,
    [IO.Path]::AltDirectorySeparatorChar) + [IO.Path]::DirectorySeparatorChar
if ($destination.StartsWith($sourcePrefix, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The archive destination must be outside the source directory.'
}

$items = @(Get-ChildItem -LiteralPath $source -Force -Recurse)
$links = @($items | Where-Object {
        ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0
    })
if ($links.Count -ne 0) {
    throw "The archive source contains a filesystem link: '$($links[0].FullName)'."
}
$files = @($items | Where-Object { -not $_.PSIsContainer })
if ($files.Count -eq 0) {
    throw 'The archive source directory contains no files.'
}

$parent = [IO.Path]::GetDirectoryName($destination)
if ([string]::IsNullOrWhiteSpace($parent)) {
    throw 'The archive destination has no parent directory.'
}
[IO.Directory]::CreateDirectory($parent) | Out-Null
if ((Test-Path -LiteralPath $destination) -and -not $Force) {
    throw "The archive destination already exists: '$destination'."
}
if ((Test-Path -LiteralPath $destination -PathType Container)) {
    throw 'The archive destination must not be a directory.'
}

$temporary = Join-Path $parent (
    '.' + [IO.Path]::GetFileName($destination) + '.' +
    [Guid]::NewGuid().ToString('N') + '.tmp')
$timestamp = [DateTimeOffset]::new(1980, 1, 1, 0, 0, 0, [TimeSpan]::Zero)

try {
    $stream = [IO.File]::Open(
        $temporary,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::ReadWrite,
        [IO.FileShare]::None)
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true,
            [Text.Encoding]::UTF8)
        try {
            foreach ($file in @($files | Sort-Object {
                        $_.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
                    } -CaseSensitive)) {
                $relative = $file.FullName.Substring($sourcePrefix.Length).Replace('\', '/')
                if ($relative.StartsWith('/') -or
                    @($relative.Split('/') | Where-Object { $_ -in @('', '.', '..') }).Count -ne 0) {
                    throw "The archive entry path is unsafe: '$relative'."
                }
                $entry = $archive.CreateEntry(
                    "$RootDirectoryName/$relative",
                    [IO.Compression.CompressionLevel]::Optimal)
                $entry.LastWriteTime = $timestamp
                $input = [IO.File]::Open(
                    $file.FullName,
                    [IO.FileMode]::Open,
                    [IO.FileAccess]::Read,
                    [IO.FileShare]::Read)
                try {
                    $output = $entry.Open()
                    try {
                        $input.CopyTo($output)
                    }
                    finally {
                        $output.Dispose()
                    }
                }
                finally {
                    $input.Dispose()
                }
            }
        }
        finally {
            $archive.Dispose()
        }
    }
    finally {
        $stream.Dispose()
    }

    if (Test-Path -LiteralPath $destination) {
        [IO.File]::Delete($destination)
    }
    [IO.File]::Move($temporary, $destination)
}
finally {
    if (Test-Path -LiteralPath $temporary -PathType Leaf) {
        [IO.File]::Delete($temporary)
    }
}

Get-Item -LiteralPath $destination
