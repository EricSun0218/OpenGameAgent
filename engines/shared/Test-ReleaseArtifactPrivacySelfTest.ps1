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
            try {
                $value = $Entries[$entryName]
                if ($value -is [byte[]]) {
                    $stream.Write($value, 0, $value.Length)
                }
                else {
                    $writer = New-Object IO.StreamWriter(
                        $stream,
                        [Text.UTF8Encoding]::new($false))
                    try {
                        $writer.Write([string]$value)
                    }
                    finally {
                        $writer.Dispose()
                    }
                }
            }
            finally {
                $stream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

function New-TestGzip {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [byte[]]$Payload
    )

    $file = [IO.File]::Create($Path)
    try {
        $gzip = [IO.Compression.GZipStream]::new(
            $file,
            [IO.Compression.CompressionMode]::Compress,
            $true)
        try {
            $gzip.Write($Payload, 0, $Payload.Length)
        }
        finally {
            $gzip.Dispose()
        }
    }
    finally {
        $file.Dispose()
    }
}

function Get-TestAdler32 {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    $a = [uint64]1
    $b = [uint64]0
    foreach ($value in $Bytes) {
        $a = ($a + $value) % 65521
        $b = ($b + $a) % 65521
    }

    return [uint32](($b -shl 16) -bor $a)
}

function New-TestZlib {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [byte[]]$Payload
    )

    $raw = [IO.MemoryStream]::new()
    try {
        $deflate = [IO.Compression.DeflateStream]::new(
            $raw,
            [IO.Compression.CompressionMode]::Compress,
            $true)
        try {
            $deflate.Write($Payload, 0, $Payload.Length)
        }
        finally {
            $deflate.Dispose()
        }
        $compressed = $raw.ToArray()
    }
    finally {
        $raw.Dispose()
    }

    $bytes = New-Object byte[] (2 + $compressed.Length + 4)
    $bytes[0] = 0x78
    $bytes[1] = 0x9c
    [Buffer]::BlockCopy($compressed, 0, $bytes, 2, $compressed.Length)
    $adler = Get-TestAdler32 -Bytes $Payload
    $trailer = 2 + $compressed.Length
    $bytes[$trailer] = [byte](($adler -shr 24) -band 0xff)
    $bytes[$trailer + 1] = [byte](($adler -shr 16) -band 0xff)
    $bytes[$trailer + 2] = [byte](($adler -shr 8) -band 0xff)
    $bytes[$trailer + 3] = [byte]($adler -band 0xff)
    [IO.File]::WriteAllBytes($Path, $bytes)
}

function Set-TarField {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Header,

        [Parameter(Mandatory)]
        [int]$Offset,

        [Parameter(Mandatory)]
        [int]$Length,

        [Parameter(Mandatory)]
        [string]$Value
    )

    $encoded = [Text.Encoding]::ASCII.GetBytes($Value)
    if ($encoded.Length -gt $Length) {
        throw 'The tar test field is too long.'
    }
    [Buffer]::BlockCopy(
        $encoded,
        0,
        $Header,
        $Offset,
        $encoded.Length)
}

function New-TestTar {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$EntryName,

        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [byte[]]$Payload,

        [switch]$V7,

        [byte]$Type = 0x30,

        [string]$LinkName = '',

        [string]$UserName = '',

        [string]$GroupName = ''
    )

    if ($Payload.Length -gt 511) {
        throw 'The tar self-test payload must fit in one block.'
    }
    $archive = New-Object byte[] 2048
    Set-TarField -Header $archive -Offset 0 -Length 100 -Value $EntryName
    Set-TarField -Header $archive -Offset 100 -Length 8 -Value "0000644`0"
    Set-TarField -Header $archive -Offset 108 -Length 8 -Value "0000000`0"
    Set-TarField -Header $archive -Offset 116 -Length 8 -Value "0000000`0"
    Set-TarField `
        -Header $archive `
        -Offset 124 `
        -Length 12 `
        -Value (
            [Convert]::ToString($Payload.Length, 8).PadLeft(11, '0') +
            "`0")
    Set-TarField -Header $archive -Offset 136 -Length 12 -Value "00000000000`0"
    for ($index = 148; $index -lt 156; $index++) {
        $archive[$index] = 0x20
    }
    $archive[156] = $Type
    if ($LinkName.Length -gt 0) {
        Set-TarField -Header $archive -Offset 157 -Length 100 -Value $LinkName
    }
    if (-not $V7) {
        Set-TarField -Header $archive -Offset 257 -Length 6 -Value "ustar`0"
        Set-TarField -Header $archive -Offset 263 -Length 2 -Value '00'
        if ($UserName.Length -gt 0) {
            Set-TarField -Header $archive -Offset 265 -Length 32 -Value $UserName
        }
        if ($GroupName.Length -gt 0) {
            Set-TarField -Header $archive -Offset 297 -Length 32 -Value $GroupName
        }
    }
    $checksum = 0
    for ($index = 0; $index -lt 512; $index++) {
        $checksum += $archive[$index]
    }
    Set-TarField `
        -Header $archive `
        -Offset 148 `
        -Length 8 `
        -Value (
            [Convert]::ToString($checksum, 8).PadLeft(6, '0') +
            "`0 ")
    [Buffer]::BlockCopy(
        $Payload,
        0,
        $archive,
        512,
        $Payload.Length)
    [IO.File]::WriteAllBytes($Path, $archive)
}

function Find-TestZipEndRecordOffset {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    for ($offset = $Bytes.Length - 22; $offset -ge 0; $offset--) {
        if ($Bytes[$offset] -eq 0x50 -and
            $Bytes[$offset + 1] -eq 0x4b -and
            $Bytes[$offset + 2] -eq 0x05 -and
            $Bytes[$offset + 3] -eq 0x06) {
            return $offset
        }
    }

    throw 'The ZIP self-test fixture has no end record.'
}

function Add-TestBytes {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset,

        [Parameter(Mandatory)]
        [byte[]]$Inserted
    )

    $result = New-Object byte[] ($Bytes.Length + $Inserted.Length)
    [Buffer]::BlockCopy($Bytes, 0, $result, 0, $Offset)
    [Buffer]::BlockCopy(
        $Inserted,
        0,
        $result,
        $Offset,
        $Inserted.Length)
    [Buffer]::BlockCopy(
        $Bytes,
        $Offset,
        $result,
        $Offset + $Inserted.Length,
        $Bytes.Length - $Offset)
    return $result
}

function Set-TestUInt16 {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset,

        [Parameter(Mandatory)]
        [uint16]$Value
    )

    [Buffer]::BlockCopy(
        [BitConverter]::GetBytes($Value),
        0,
        $Bytes,
        $Offset,
        2)
}

function Set-TestUInt32 {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset,

        [Parameter(Mandatory)]
        [uint32]$Value
    )

    [Buffer]::BlockCopy(
        [BitConverter]::GetBytes($Value),
        0,
        $Bytes,
        $Offset,
        4)
}

function Update-TestTarChecksum {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    for ($index = 148; $index -lt 156; $index++) {
        $Bytes[$index] = 0x20
    }
    $checksum = 0
    for ($index = 0; $index -lt 512; $index++) {
        $checksum += $Bytes[$index]
    }
    Set-TarField `
        -Header $Bytes `
        -Offset 148 `
        -Length 8 `
        -Value (
            [Convert]::ToString($checksum, 8).PadLeft(6, '0') +
            "`0 ")
}

function Update-TestTarSignedChecksum {
    param([Parameter(Mandatory)][byte[]]$Bytes)

    for ($index = 148; $index -lt 156; $index++) {
        $Bytes[$index] = 0x20
    }
    $checksum = 0
    for ($index = 0; $index -lt 512; $index++) {
        $checksum += if ($Bytes[$index] -ge 0x80) {
            [int]$Bytes[$index] - 256
        }
        else {
            $Bytes[$index]
        }
    }
    Set-TarField `
        -Header $Bytes `
        -Offset 148 `
        -Length 8 `
        -Value (
            [Convert]::ToString($checksum, 8).PadLeft(6, '0') +
            "`0 ")
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

    $encodingCases = @(
        @{
            Name = 'utf16le'
            Encoding = [Text.Encoding]::Unicode
            Alignments = 2
        },
        @{
            Name = 'utf16be'
            Encoding = [Text.Encoding]::BigEndianUnicode
            Alignments = 2
        },
        @{
            Name = 'utf32le'
            Encoding = [Text.Encoding]::UTF32
            Alignments = 4
        },
        @{
            Name = 'utf32be'
            Encoding = [Text.UTF32Encoding]::new($true, $false)
            Alignments = 4
        })
    foreach ($encodingCase in $encodingCases) {
        $unicodeMarkerBytes = $encodingCase.Encoding.GetBytes($deniedMarker)
        for ($alignment = 0;
             $alignment -lt $encodingCase.Alignments;
             $alignment++) {
            $unalignedUnicodeArchive = Join-Path (
                $temporaryRoot
            ) (
                'unicode-' + $encodingCase.Name + '-' + $alignment + '.zip')
            $unalignedUnicodeBytes = New-Object byte[] (
                $alignment + $unicodeMarkerBytes.Length)
            for ($prefixIndex = 0;
                 $prefixIndex -lt $alignment;
                 $prefixIndex++) {
                $unalignedUnicodeBytes[$prefixIndex] = 0x41
            }
            [Buffer]::BlockCopy(
                $unicodeMarkerBytes,
                0,
                $unalignedUnicodeBytes,
                $alignment,
                $unicodeMarkerBytes.Length)
            New-TestArchive `
                -ArchivePath $unalignedUnicodeArchive `
                -Entries @{
                    'payload.data' = $unalignedUnicodeBytes
                }
            Invoke-ScannerReject `
                -Target $unalignedUnicodeArchive `
                -DeniedRegex $deniedMarker `
                -MustNotEcho $deniedMarker
        }
    }

    $nestedInner = Join-Path $temporaryRoot 'nested-inner.bin'
    New-TestArchive `
        -ArchivePath $nestedInner `
        -Entries @{
            'payload.opaque' = $deniedMarker
        }
    $nestedOuter = Join-Path $temporaryRoot 'nested-outer.zip'
    New-TestArchive `
        -ArchivePath $nestedOuter `
        -Entries @{
            'payload.opaque' = [IO.File]::ReadAllBytes($nestedInner)
        }
    Invoke-ScannerReject `
        -Target $nestedOuter `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $prefixedArchive = Join-Path $temporaryRoot 'prefixed-archive.bin'
    $prefix = [byte[]](1, 2, 3, 4, 5, 6, 7, 8)
    $archiveBytes = [IO.File]::ReadAllBytes($nestedInner)
    $prefixedBytes = New-Object byte[] (
        $prefix.Length + $archiveBytes.Length)
    [Buffer]::BlockCopy(
        $prefix,
        0,
        $prefixedBytes,
        0,
        $prefix.Length)
    [Buffer]::BlockCopy(
        $archiveBytes,
        0,
        $prefixedBytes,
        $prefix.Length,
        $archiveBytes.Length)
    [IO.File]::WriteAllBytes($prefixedArchive, $prefixedBytes)
    Invoke-ScannerReject `
        -Target $prefixedArchive `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $concatenatedZip = Join-Path $temporaryRoot 'concatenated-zip.bin'
    $deniedZipBytes = [IO.File]::ReadAllBytes($nestedInner)
    $safeZipBytes = [IO.File]::ReadAllBytes($safeArchive)
    $concatenatedZipBytes = New-Object byte[] (
        $deniedZipBytes.Length + $safeZipBytes.Length)
    [Buffer]::BlockCopy(
        $deniedZipBytes,
        0,
        $concatenatedZipBytes,
        0,
        $deniedZipBytes.Length)
    [Buffer]::BlockCopy(
        $safeZipBytes,
        0,
        $concatenatedZipBytes,
        $deniedZipBytes.Length,
        $safeZipBytes.Length)
    [IO.File]::WriteAllBytes($concatenatedZip, $concatenatedZipBytes)
    Invoke-ScannerReject `
        -Target $concatenatedZip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $directoryGapZip = Join-Path $temporaryRoot 'directory-gap.zip'
    $safeEndOffset = Find-TestZipEndRecordOffset -Bytes $safeZipBytes
    $directoryGapBytes = New-Object byte[] ($safeZipBytes.Length + 1)
    [Buffer]::BlockCopy(
        $safeZipBytes,
        0,
        $directoryGapBytes,
        0,
        $safeEndOffset)
    $directoryGapBytes[$safeEndOffset] = 0x41
    [Buffer]::BlockCopy(
        $safeZipBytes,
        $safeEndOffset,
        $directoryGapBytes,
        $safeEndOffset + 1,
        $safeZipBytes.Length - $safeEndOffset)
    [IO.File]::WriteAllBytes($directoryGapZip, $directoryGapBytes)
    Invoke-ScannerReject -Target $directoryGapZip

    $invalidZipCrc = Join-Path $temporaryRoot 'invalid-crc.zip'
    $invalidZipCrcBytes = [byte[]]$safeZipBytes.Clone()
    $invalidZipCrcEnd = Find-TestZipEndRecordOffset -Bytes $invalidZipCrcBytes
    $invalidZipCrcDirectory = [BitConverter]::ToUInt32(
        $invalidZipCrcBytes,
        $invalidZipCrcEnd + 16)
    $invalidZipCrcLocal = [BitConverter]::ToUInt32(
        $invalidZipCrcBytes,
        [int]$invalidZipCrcDirectory + 42)
    $invalidZipCrcValue = (
        [BitConverter]::ToUInt32(
            $invalidZipCrcBytes,
            [int]$invalidZipCrcDirectory + 16) -bxor 0x00000001)
    Set-TestUInt32 `
        -Bytes $invalidZipCrcBytes `
        -Offset ([int]$invalidZipCrcDirectory + 16) `
        -Value $invalidZipCrcValue
    Set-TestUInt32 `
        -Bytes $invalidZipCrcBytes `
        -Offset ([int]$invalidZipCrcLocal + 14) `
        -Value $invalidZipCrcValue
    [IO.File]::WriteAllBytes($invalidZipCrc, $invalidZipCrcBytes)
    Invoke-ScannerReject -Target $invalidZipCrc

    $forgedZeroZip = Join-Path $temporaryRoot 'forged-zero-size.zip'
    $forgedZeroBytes = [IO.File]::ReadAllBytes($nestedInner)
    $forgedEndOffset = Find-TestZipEndRecordOffset -Bytes $forgedZeroBytes
    $forgedDirectoryOffset = [BitConverter]::ToUInt32(
        $forgedZeroBytes,
        $forgedEndOffset + 16)
    Set-TestUInt32 `
        -Bytes $forgedZeroBytes `
        -Offset ([int]$forgedDirectoryOffset + 24) `
        -Value 0
    [IO.File]::WriteAllBytes($forgedZeroZip, $forgedZeroBytes)
    Invoke-ScannerReject `
        -Target $forgedZeroZip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $renamedGzip = Join-Path $temporaryRoot 'renamed-gzip.opaque'
    New-TestGzip `
        -Path $renamedGzip `
        -Payload ([Text.Encoding]::UTF8.GetBytes($deniedMarker))
    Invoke-ScannerReject `
        -Target $renamedGzip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $renamedZlib = Join-Path $temporaryRoot 'renamed-zlib.opaque'
    New-TestZlib `
        -Path $renamedZlib `
        -Payload ([Text.Encoding]::UTF8.GetBytes($deniedMarker))
    Invoke-ScannerReject `
        -Target $renamedZlib `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker
    $deniedZlibBytes = [IO.File]::ReadAllBytes($renamedZlib)

    $dictionaryZlibArchive = Join-Path (
        $temporaryRoot
    ) 'preset-dictionary-zlib.zip'
    $dictionaryZlibBytes = [Convert]::FromBase64String(
        'ePliNQiSS8IqCgBiNQiS')
    New-TestArchive `
        -ArchivePath $dictionaryZlibArchive `
        -Entries @{
            'payload.data' = $dictionaryZlibBytes
        }
    Invoke-ScannerReject `
        -Target $dictionaryZlibArchive `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $prefixedDictionaryZlib = Join-Path (
        $temporaryRoot
    ) 'prefixed-preset-dictionary-zlib.opaque'
    $dictionaryPrefix = [Text.Encoding]::ASCII.GetBytes('benign-prefix')
    $prefixedDictionaryBytes = New-Object byte[] (
        $dictionaryPrefix.Length + $dictionaryZlibBytes.Length)
    [Buffer]::BlockCopy(
        $dictionaryPrefix,
        0,
        $prefixedDictionaryBytes,
        0,
        $dictionaryPrefix.Length)
    [Buffer]::BlockCopy(
        $dictionaryZlibBytes,
        0,
        $prefixedDictionaryBytes,
        $dictionaryPrefix.Length,
        $dictionaryZlibBytes.Length)
    [IO.File]::WriteAllBytes(
        $prefixedDictionaryZlib,
        $prefixedDictionaryBytes)
    Invoke-ScannerReject -Target $prefixedDictionaryZlib

    $candidateFlood = Join-Path $temporaryRoot 'zlib-candidate-flood.opaque'
    $candidateFloodBytes = New-Object byte[] 10000
    for ($candidateIndex = 0;
         $candidateIndex -lt $candidateFloodBytes.Length;
         $candidateIndex += 2) {
        $candidateFloodBytes[$candidateIndex] = 0x78
        $candidateFloodBytes[$candidateIndex + 1] = 0x9c
    }
    [IO.File]::WriteAllBytes($candidateFlood, $candidateFloodBytes)
    Invoke-ScannerReject -Target $candidateFlood

    $prefixedZlib = Join-Path $temporaryRoot 'prefixed-zlib.opaque'
    $zlibPrefix = [Text.Encoding]::ASCII.GetBytes('safe-prefix')
    $prefixedZlibBytes = New-Object byte[] (
        $zlibPrefix.Length + $deniedZlibBytes.Length)
    [Buffer]::BlockCopy(
        $zlibPrefix,
        0,
        $prefixedZlibBytes,
        0,
        $zlibPrefix.Length)
    [Buffer]::BlockCopy(
        $deniedZlibBytes,
        0,
        $prefixedZlibBytes,
        $zlibPrefix.Length,
        $deniedZlibBytes.Length)
    [IO.File]::WriteAllBytes($prefixedZlib, $prefixedZlibBytes)
    Invoke-ScannerReject `
        -Target $prefixedZlib `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $reducedWindowCandidate = Join-Path (
        $temporaryRoot
    ) 'invalid-reduced-window-zlib.opaque'
    $reducedWindowBytes = [Convert]::FromBase64String(
        'CNdjYGRiZmFlY+fg5OLm4eXjFxAUEhYRFROXkJSSlpGVk1dQVFJWUVVT19DU0tbR1dM3MDQyNjE1M7ewtLK2sbWzd3B0cnZxdXP38PTy9vH18w8IDAoOCQ0Lj4iMio6JjYtPSExKTklNS8/IzMrOyc3LLygsKi4pLSuvqKyqrqmtq29obGpuaW1r7+js6u7p7eufMHHS5ClTp02fMXPW7Dlz581fsHDR4iVLly1fsXLV6jVr163fsHHT5i1bt23fsXPX7j179+0/cPDQ4SNHjx0/cfLU6TNnz52/cPHS5StXr12/cfPW7Tt3791/8PDR4ydPnz1/8fLV6zdv373/8PHT5y9fv33/8fPX7z9///2PYBjhAQAAu1T/WQ==')
    $reducedWindowPrefix = [Text.Encoding]::ASCII.GetBytes('prefix')
    $wrappedReducedWindow = New-Object byte[] (
        $reducedWindowPrefix.Length + $reducedWindowBytes.Length)
    [Buffer]::BlockCopy(
        $reducedWindowPrefix,
        0,
        $wrappedReducedWindow,
        0,
        $reducedWindowPrefix.Length)
    [Buffer]::BlockCopy(
        $reducedWindowBytes,
        0,
        $wrappedReducedWindow,
        $reducedWindowPrefix.Length,
        $reducedWindowBytes.Length)
    [IO.File]::WriteAllBytes(
        $reducedWindowCandidate,
        $wrappedReducedWindow)
    Invoke-ScannerPass -Target $reducedWindowCandidate

    $incompleteHuffmanCandidate = Join-Path (
        $temporaryRoot
    ) 'invalid-incomplete-huffman-zlib.opaque'
    $incompleteHuffmanBytes = [Convert]::FromBase64String(
        'eJwFAAAEAAAAAA==')
    $wrappedIncompleteHuffman = New-Object byte[] (
        $zlibPrefix.Length + $incompleteHuffmanBytes.Length)
    [Buffer]::BlockCopy(
        $zlibPrefix,
        0,
        $wrappedIncompleteHuffman,
        0,
        $zlibPrefix.Length)
    [Buffer]::BlockCopy(
        $incompleteHuffmanBytes,
        0,
        $wrappedIncompleteHuffman,
        $zlibPrefix.Length,
        $incompleteHuffmanBytes.Length)
    [IO.File]::WriteAllBytes(
        $incompleteHuffmanCandidate,
        $wrappedIncompleteHuffman)
    Invoke-ScannerPass -Target $incompleteHuffmanCandidate

    $nestedZlib = Join-Path $temporaryRoot 'nested-zlib.zip'
    New-TestArchive `
        -ArchivePath $nestedZlib `
        -Entries @{
            'payload.data' = $deniedZlibBytes
        }
    Invoke-ScannerReject `
        -Target $nestedZlib `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $prefixedGzip = Join-Path $temporaryRoot 'prefixed-gzip.opaque'
    $deniedGzipBytes = [IO.File]::ReadAllBytes($renamedGzip)

    $deflateTailZip = Join-Path $temporaryRoot 'deflate-tail-carrier.zip'
    $deflateBaseZip = Join-Path $temporaryRoot 'deflate-tail-base.zip'
    New-TestArchive `
        -ArchivePath $deflateBaseZip `
        -Entries @{
            'payload.txt' = ('safe-content-' * 128)
        }
    $deflateTailBytes = [IO.File]::ReadAllBytes($deflateBaseZip)
    if ([BitConverter]::ToUInt16($deflateTailBytes, 8) -ne 8) {
        throw 'The deflate-tail fixture was not compressed with DEFLATE.'
    }
    $deflateTailEnd = Find-TestZipEndRecordOffset -Bytes $deflateTailBytes
    $deflateTailDirectory = [BitConverter]::ToUInt32(
        $deflateTailBytes,
        $deflateTailEnd + 16)
    $deflateTailOriginalSize = [BitConverter]::ToUInt32(
        $deflateTailBytes,
        18)
    $deflateTailBytes = Add-TestBytes `
        -Bytes $deflateTailBytes `
        -Offset ([int]$deflateTailDirectory) `
        -Inserted $deniedGzipBytes
    $deflateTailNewSize = [uint32](
        $deflateTailOriginalSize + $deniedGzipBytes.Length)
    Set-TestUInt32 `
        -Bytes $deflateTailBytes `
        -Offset 18 `
        -Value $deflateTailNewSize
    $deflateTailDirectory = [uint32](
        $deflateTailDirectory + $deniedGzipBytes.Length)
    Set-TestUInt32 `
        -Bytes $deflateTailBytes `
        -Offset ([int]$deflateTailDirectory + 20) `
        -Value $deflateTailNewSize
    $deflateTailEnd += $deniedGzipBytes.Length
    Set-TestUInt32 `
        -Bytes $deflateTailBytes `
        -Offset ($deflateTailEnd + 16) `
        -Value $deflateTailDirectory
    [IO.File]::WriteAllBytes($deflateTailZip, $deflateTailBytes)
    Invoke-ScannerReject `
        -Target $deflateTailZip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $gzipPrefix = [Text.Encoding]::ASCII.GetBytes('benign-prefix')
    $prefixedGzipBytes = New-Object byte[] (
        $gzipPrefix.Length + $deniedGzipBytes.Length)
    [Buffer]::BlockCopy(
        $gzipPrefix,
        0,
        $prefixedGzipBytes,
        0,
        $gzipPrefix.Length)
    [Buffer]::BlockCopy(
        $deniedGzipBytes,
        0,
        $prefixedGzipBytes,
        $gzipPrefix.Length,
        $deniedGzipBytes.Length)
    [IO.File]::WriteAllBytes($prefixedGzip, $prefixedGzipBytes)
    Invoke-ScannerReject `
        -Target $prefixedGzip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $malformedPrefixedGzip = Join-Path (
        $temporaryRoot
    ) 'malformed-prefixed-gzip.opaque'
    $malformedGzipBytes = [byte[]]$deniedGzipBytes.Clone()
    $malformedGzipBytes[$malformedGzipBytes.Length - 4] = (
        $malformedGzipBytes[$malformedGzipBytes.Length - 4] -bxor 0xff)
    $malformedPrefixedBytes = New-Object byte[] (
        $gzipPrefix.Length + $malformedGzipBytes.Length)
    [Buffer]::BlockCopy(
        $gzipPrefix,
        0,
        $malformedPrefixedBytes,
        0,
        $gzipPrefix.Length)
    [Buffer]::BlockCopy(
        $malformedGzipBytes,
        0,
        $malformedPrefixedBytes,
        $gzipPrefix.Length,
        $malformedGzipBytes.Length)
    [IO.File]::WriteAllBytes(
        $malformedPrefixedGzip,
        $malformedPrefixedBytes)
    Invoke-ScannerReject `
        -Target $malformedPrefixedGzip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $nestedPrefixedGzip = Join-Path $temporaryRoot 'nested-prefixed-gzip.zip'
    New-TestArchive `
        -ArchivePath $nestedPrefixedGzip `
        -Entries @{
            'nested/payload.bin' = $prefixedGzipBytes
        }
    Invoke-ScannerReject `
        -Target $nestedPrefixedGzip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $safeRenamedGzip = Join-Path $temporaryRoot 'safe-renamed-gzip.opaque'
    New-TestGzip `
        -Path $safeRenamedGzip `
        -Payload ([Text.Encoding]::UTF8.GetBytes('safe gzip content'))
    Invoke-ScannerPass -Target $safeRenamedGzip

    $invalidCrcGzip = Join-Path $temporaryRoot 'invalid-crc-gzip.opaque'
    $invalidCrcGzipBytes = [byte[]](
        [IO.File]::ReadAllBytes($safeRenamedGzip).Clone())
    $invalidCrcGzipBytes[$invalidCrcGzipBytes.Length - 8] = (
        $invalidCrcGzipBytes[$invalidCrcGzipBytes.Length - 8] -bxor 0x01)
    [IO.File]::WriteAllBytes($invalidCrcGzip, $invalidCrcGzipBytes)
    Invoke-ScannerReject -Target $invalidCrcGzip

    $optionalHeaderGzip = Join-Path (
        $temporaryRoot
    ) 'optional-header-gzip.opaque'
    $safeGzipBytes = [IO.File]::ReadAllBytes($safeRenamedGzip)
    $optionalHeaderBytes = New-Object byte[] (
        $safeGzipBytes.Length + 2 + $deniedGzipBytes.Length)
    [Buffer]::BlockCopy(
        $safeGzipBytes,
        0,
        $optionalHeaderBytes,
        0,
        10)
    $optionalHeaderBytes[3] = 0x04
    Set-TestUInt16 `
        -Bytes $optionalHeaderBytes `
        -Offset 10 `
        -Value ([uint16]$deniedGzipBytes.Length)
    [Buffer]::BlockCopy(
        $deniedGzipBytes,
        0,
        $optionalHeaderBytes,
        12,
        $deniedGzipBytes.Length)
    [Buffer]::BlockCopy(
        $safeGzipBytes,
        10,
        $optionalHeaderBytes,
        12 + $deniedGzipBytes.Length,
        $safeGzipBytes.Length - 10)
    [IO.File]::WriteAllBytes($optionalHeaderGzip, $optionalHeaderBytes)
    Invoke-ScannerReject `
        -Target $optionalHeaderGzip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $metadataCarrier = New-Object byte[] (4 + $deniedGzipBytes.Length)
    $metadataCarrier[0] = 0xfe
    $metadataCarrier[1] = 0xca
    Set-TestUInt16 `
        -Bytes $metadataCarrier `
        -Offset 2 `
        -Value ([uint16]$deniedGzipBytes.Length)
    [Buffer]::BlockCopy(
        $deniedGzipBytes,
        0,
        $metadataCarrier,
        4,
        $deniedGzipBytes.Length)

    $localExtraZip = Join-Path $temporaryRoot 'local-extra-carrier.zip'
    $localExtraBytes = [byte[]]$safeZipBytes.Clone()
    $localNameLength = [BitConverter]::ToUInt16($localExtraBytes, 26)
    $localInsertOffset = 30 + $localNameLength
    $localExtraBytes = Add-TestBytes `
        -Bytes $localExtraBytes `
        -Offset $localInsertOffset `
        -Inserted $metadataCarrier
    Set-TestUInt16 `
        -Bytes $localExtraBytes `
        -Offset 28 `
        -Value ([uint16]$metadataCarrier.Length)
    $localExtraEnd = Find-TestZipEndRecordOffset -Bytes $localExtraBytes
    $localExtraDirectory = [BitConverter]::ToUInt32(
        $localExtraBytes,
        $localExtraEnd + 16)
    Set-TestUInt32 `
        -Bytes $localExtraBytes `
        -Offset ($localExtraEnd + 16) `
        -Value ([uint32]($localExtraDirectory + $metadataCarrier.Length))
    [IO.File]::WriteAllBytes($localExtraZip, $localExtraBytes)
    Invoke-ScannerReject `
        -Target $localExtraZip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $centralExtraZip = Join-Path $temporaryRoot 'central-extra-carrier.zip'
    $centralExtraBytes = [byte[]]$safeZipBytes.Clone()
    $centralExtraEnd = Find-TestZipEndRecordOffset -Bytes $centralExtraBytes
    $centralOffset = [BitConverter]::ToUInt32(
        $centralExtraBytes,
        $centralExtraEnd + 16)
    $centralNameLength = [BitConverter]::ToUInt16(
        $centralExtraBytes,
        [int]$centralOffset + 28)
    $centralInsertOffset = [int]$centralOffset + 46 + $centralNameLength
    $centralExtraBytes = Add-TestBytes `
        -Bytes $centralExtraBytes `
        -Offset $centralInsertOffset `
        -Inserted $metadataCarrier
    Set-TestUInt16 `
        -Bytes $centralExtraBytes `
        -Offset ([int]$centralOffset + 30) `
        -Value ([uint16]$metadataCarrier.Length)
    $centralExtraEnd += $metadataCarrier.Length
    $centralDirectoryBytes = [BitConverter]::ToUInt32(
        $centralExtraBytes,
        $centralExtraEnd + 12)
    Set-TestUInt32 `
        -Bytes $centralExtraBytes `
        -Offset ($centralExtraEnd + 12) `
        -Value ([uint32](
            $centralDirectoryBytes + $metadataCarrier.Length))
    [IO.File]::WriteAllBytes($centralExtraZip, $centralExtraBytes)
    Invoke-ScannerReject `
        -Target $centralExtraZip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $endCommentZip = Join-Path $temporaryRoot 'end-comment-carrier.zip'
    $endCommentBytes = Add-TestBytes `
        -Bytes $safeZipBytes `
        -Offset $safeZipBytes.Length `
        -Inserted $deniedGzipBytes
    $endCommentOffset = Find-TestZipEndRecordOffset -Bytes $safeZipBytes
    Set-TestUInt16 `
        -Bytes $endCommentBytes `
        -Offset ($endCommentOffset + 20) `
        -Value ([uint16]$deniedGzipBytes.Length)
    [IO.File]::WriteAllBytes($endCommentZip, $endCommentBytes)
    Invoke-ScannerReject `
        -Target $endCommentZip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $renamedTar = Join-Path $temporaryRoot 'renamed-tar.opaque'
    New-TestTar `
        -Path $renamedTar `
        -EntryName 'nested/payload.txt' `
        -Payload ([Text.Encoding]::UTF8.GetBytes($deniedMarker))
    Invoke-ScannerReject `
        -Target $renamedTar `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $renamedV7Tar = Join-Path $temporaryRoot 'renamed-v7-tar.opaque'
    New-TestTar `
        -Path $renamedV7Tar `
        -EntryName 'nested/payload.txt' `
        -Payload ([Text.Encoding]::UTF8.GetBytes($deniedMarker)) `
        -V7
    Invoke-ScannerReject `
        -Target $renamedV7Tar `
        -DeniedRegex ('^' + [regex]::Escape($deniedMarker) + '$') `
        -MustNotEcho $deniedMarker

    $unsupportedTar = Join-Path $temporaryRoot 'unsupported-tar.opaque'
    New-TestTar `
        -Path $unsupportedTar `
        -EntryName 'metadata' `
        -Payload ([Text.Encoding]::UTF8.GetBytes('safe metadata')) `
        -Type 0x78
    Invoke-ScannerReject -Target $unsupportedTar

    $unsupportedV7Tar = Join-Path (
        $temporaryRoot
    ) 'unsupported-v7-tar.opaque'
    New-TestTar `
        -Path $unsupportedV7Tar `
        -EntryName 'metadata' `
        -Payload ([byte[]](0x00)) `
        -Type 0x32 `
        -LinkName 'safe-target' `
        -V7
    Invoke-ScannerReject -Target $unsupportedV7Tar

    $nonAsciiV7Tar = Join-Path $temporaryRoot 'non-ascii-v7-tar.opaque'
    New-TestTar `
        -Path $nonAsciiV7Tar `
        -EntryName 'safe-name' `
        -Payload ([byte[]](0x00)) `
        -V7
    $nonAsciiV7Bytes = [IO.File]::ReadAllBytes($nonAsciiV7Tar)
    $nonAsciiV7Bytes[0] = 0x80
    Update-TestTarChecksum -Bytes $nonAsciiV7Bytes
    [IO.File]::WriteAllBytes($nonAsciiV7Tar, $nonAsciiV7Bytes)
    Invoke-ScannerReject -Target $nonAsciiV7Tar

    $tarMetadataCarrier = Join-Path $temporaryRoot 'tar-metadata-carrier.opaque'
    New-TestTar `
        -Path $tarMetadataCarrier `
        -EntryName 'safe-name' `
        -Payload ([Text.Encoding]::UTF8.GetBytes('safe content')) `
        -UserName $deniedMarker
    Invoke-ScannerReject `
        -Target $tarMetadataCarrier `
        -DeniedRegex ('^' + [regex]::Escape($deniedMarker) + '$') `
        -MustNotEcho $deniedMarker

    $tarTextSlack = Join-Path $temporaryRoot 'tar-text-slack.opaque'
    New-TestTar `
        -Path $tarTextSlack `
        -EntryName 'safe-name' `
        -Payload ([Text.Encoding]::UTF8.GetBytes('safe content')) `
        -UserName 'safe'
    $tarTextSlackBytes = [IO.File]::ReadAllBytes($tarTextSlack)
    $tarTextSlackBytes[270] = 0x78
    Update-TestTarChecksum -Bytes $tarTextSlackBytes
    [IO.File]::WriteAllBytes($tarTextSlack, $tarTextSlackBytes)
    Invoke-ScannerReject -Target $tarTextSlack

    $safeRenamedTar = Join-Path $temporaryRoot 'safe-renamed-tar.opaque'
    New-TestTar `
        -Path $safeRenamedTar `
        -EntryName 'nested/payload.txt' `
        -Payload ([Text.Encoding]::UTF8.GetBytes('safe tar content'))
    Invoke-ScannerPass -Target $safeRenamedTar

    $plainTarText = Join-Path $temporaryRoot 'plain-tar-text.txt'
    $plainTarTextBytes = New-Object byte[] 1024
    for ($index = 0; $index -lt $plainTarTextBytes.Length; $index++) {
        $plainTarTextBytes[$index] = 0x61
    }
    [Buffer]::BlockCopy(
        [Text.Encoding]::ASCII.GetBytes('ustar'),
        0,
        $plainTarTextBytes,
        300,
        5)
    [IO.File]::WriteAllBytes($plainTarText, $plainTarTextBytes)
    Invoke-ScannerPass -Target $plainTarText

    $prefixedTar = Join-Path $temporaryRoot 'prefixed-tar.opaque'
    $safeTarBytes = [IO.File]::ReadAllBytes($safeRenamedTar)
    $prefixedTarBytes = New-Object byte[] (128 + $safeTarBytes.Length)
    for ($index = 0; $index -lt 128; $index++) {
        $prefixedTarBytes[$index] = 0x61
    }
    [Buffer]::BlockCopy(
        $safeTarBytes,
        0,
        $prefixedTarBytes,
        128,
        $safeTarBytes.Length)
    [IO.File]::WriteAllBytes($prefixedTar, $prefixedTarBytes)
    Invoke-ScannerReject -Target $prefixedTar

    $signedTar = Join-Path $temporaryRoot 'signed-checksum-tar.opaque'
    $signedTarBytes = [byte[]]$safeTarBytes.Clone()
    $signedTarBytes[500] = 0x80
    Update-TestTarSignedChecksum -Bytes $signedTarBytes
    [IO.File]::WriteAllBytes($signedTar, $signedTarBytes)
    Invoke-ScannerPass -Target $signedTar

    $prefixedSignedTar = Join-Path (
        $temporaryRoot
    ) 'prefixed-signed-checksum-tar.opaque'
    $prefixedSignedTarBytes = New-Object byte[] (
        128 + $signedTarBytes.Length)
    for ($index = 0; $index -lt 128; $index++) {
        $prefixedSignedTarBytes[$index] = 0x61
    }
    [Buffer]::BlockCopy(
        $signedTarBytes,
        0,
        $prefixedSignedTarBytes,
        128,
        $signedTarBytes.Length)
    [IO.File]::WriteAllBytes(
        $prefixedSignedTar,
        $prefixedSignedTarBytes)
    Invoke-ScannerReject -Target $prefixedSignedTar

    $boundaryTar = Join-Path $temporaryRoot 'boundary-tar.opaque'
    New-TestTar `
        -Path $boundaryTar `
        -EntryName 'outer.txt' `
        -Payload (New-Object byte[] 510)
    $boundaryTarBytes = [IO.File]::ReadAllBytes($boundaryTar)
    [Buffer]::BlockCopy(
        $safeTarBytes,
        0,
        $boundaryTarBytes,
        510,
        512)
    Update-TestTarChecksum -Bytes $boundaryTarBytes
    [IO.File]::WriteAllBytes($boundaryTar, $boundaryTarBytes)
    Invoke-ScannerReject -Target $boundaryTar

    if ($deniedGzipBytes.Length -gt 64) {
        throw 'The nested tar-header fixture exceeds its carrier field.'
    }
    $tarHeaderCarrier = Join-Path $temporaryRoot 'tar-header-carrier.opaque'
    $tarHeaderBytes = [IO.File]::ReadAllBytes($safeRenamedTar)
    [Buffer]::BlockCopy(
        $deniedGzipBytes,
        0,
        $tarHeaderBytes,
        265,
        $deniedGzipBytes.Length)
    Update-TestTarChecksum -Bytes $tarHeaderBytes
    [IO.File]::WriteAllBytes($tarHeaderCarrier, $tarHeaderBytes)
    Invoke-ScannerReject `
        -Target $tarHeaderCarrier `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $tarPaddingCarrier = Join-Path $temporaryRoot 'tar-padding-carrier.opaque'
    $tarPaddingBytes = [IO.File]::ReadAllBytes($safeRenamedTar)
    [Buffer]::BlockCopy(
        $deniedGzipBytes,
        0,
        $tarPaddingBytes,
        512 + ([Text.Encoding]::UTF8.GetByteCount('safe tar content')),
        $deniedGzipBytes.Length)
    [IO.File]::WriteAllBytes($tarPaddingCarrier, $tarPaddingBytes)
    Invoke-ScannerReject `
        -Target $tarPaddingCarrier `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $tarGzip = Join-Path $temporaryRoot 'renamed-tar-gzip.opaque'
    New-TestGzip `
        -Path $tarGzip `
        -Payload ([IO.File]::ReadAllBytes($renamedTar))
    Invoke-ScannerReject `
        -Target $tarGzip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $concatenatedGzip = Join-Path $temporaryRoot 'concatenated-gzip.opaque'
    $concatenatedBytes = New-Object byte[] (
        $safeGzipBytes.Length + $deniedGzipBytes.Length)
    [Buffer]::BlockCopy(
        $safeGzipBytes,
        0,
        $concatenatedBytes,
        0,
        $safeGzipBytes.Length)
    [Buffer]::BlockCopy(
        $deniedGzipBytes,
        0,
        $concatenatedBytes,
        $safeGzipBytes.Length,
        $deniedGzipBytes.Length)
    [IO.File]::WriteAllBytes($concatenatedGzip, $concatenatedBytes)
    Invoke-ScannerReject `
        -Target $concatenatedGzip `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $renamedSevenZip = Join-Path $temporaryRoot 'renamed-seven-zip.opaque'
    [IO.File]::WriteAllBytes(
        $renamedSevenZip,
        [byte[]](0x37, 0x7a, 0xbc, 0xaf, 0x27, 0x1c, 0, 0))
    Invoke-ScannerReject -Target $renamedSevenZip

    $renamedCab = Join-Path $temporaryRoot 'renamed-cab.opaque'
    $makeCab = Get-Command makecab.exe -ErrorAction SilentlyContinue
    if ($null -ne $makeCab) {
        $cabInput = Join-Path $temporaryRoot 'cab-payload.txt'
        [IO.File]::WriteAllText(
            $cabInput,
            $deniedMarker,
            [Text.UTF8Encoding]::new($false))
        $makeCabOutput = @(
            & $makeCab.Source `
                /D CompressionType=MSZIP `
                $cabInput `
                $renamedCab 2>&1
        )
        if ($LASTEXITCODE -ne 0 -or
            -not (Test-Path -LiteralPath $renamedCab)) {
            throw 'The compressed CAB self-test fixture could not be created.'
        }
    }
    else {
        [IO.File]::WriteAllBytes(
            $renamedCab,
            [byte[]](0x4d, 0x53, 0x43, 0x46, 0, 0, 0, 0))
    }
    Invoke-ScannerReject `
        -Target $renamedCab `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker
    $nestedCab = Join-Path $temporaryRoot 'nested-cab.zip'
    New-TestArchive `
        -ArchivePath $nestedCab `
        -Entries @{
            'payload.data' = [IO.File]::ReadAllBytes($renamedCab)
        }
    Invoke-ScannerReject `
        -Target $nestedCab `
        -DeniedRegex $deniedMarker `
        -MustNotEcho $deniedMarker

    $renamedZstandard = Join-Path $temporaryRoot 'renamed-zstandard.opaque'
    $zstandardBytes = [byte[]](0x28, 0xb5, 0x2f, 0xfd, 0, 0, 0, 0)
    [IO.File]::WriteAllBytes($renamedZstandard, $zstandardBytes)
    Invoke-ScannerReject -Target $renamedZstandard

    $skippableZstandard = Join-Path (
        $temporaryRoot
    ) 'skippable-zstandard.opaque'
    [IO.File]::WriteAllBytes(
        $skippableZstandard,
        [byte[]](0x5f, 0x2a, 0x4d, 0x18, 0, 0, 0, 0))
    Invoke-ScannerReject -Target $skippableZstandard

    $nestedZstandard = Join-Path $temporaryRoot 'nested-zstandard.zip'
    New-TestArchive `
        -ArchivePath $nestedZstandard `
        -Entries @{
            'payload.data' = $zstandardBytes
        }
    Invoke-ScannerReject -Target $nestedZstandard

    $renamedLz4 = Join-Path $temporaryRoot 'renamed-lz4.opaque'
    $lz4Bytes = [byte[]](0x04, 0x22, 0x4d, 0x18, 0, 0, 0, 0)
    [IO.File]::WriteAllBytes($renamedLz4, $lz4Bytes)
    Invoke-ScannerReject -Target $renamedLz4

    $nestedLz4 = Join-Path $temporaryRoot 'nested-lz4.zip'
    New-TestArchive `
        -ArchivePath $nestedLz4 `
        -Entries @{
            'payload.data' = $lz4Bytes
        }
    Invoke-ScannerReject -Target $nestedLz4

    $invalidArchive = Join-Path $temporaryRoot 'invalid.zip'
    [IO.File]::WriteAllText(
        $invalidArchive,
        'not an archive',
        [Text.UTF8Encoding]::new($false))
    Invoke-ScannerReject -Target $invalidArchive

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
            $null)
        $missingConfigurationRejected = $false
        try {
            $null = & $script:scanner `
                -Path $safeDirectory `
                -RequireInjectedDenyRegex
        }
        catch {
            $missingConfigurationRejected = $true
        }
        if (-not $missingConfigurationRejected) {
            throw 'The release privacy scanner accepted a missing required deny configuration.'
        }

        [Environment]::SetEnvironmentVariable(
            'GAME_AGENT_RELEASE_DENY_REGEX',
            $deniedMarker)
        $requiredOutput = @(
            & $script:scanner `
                -Path $safeDirectory `
                -RequireInjectedDenyRegex
        )
        if ($requiredOutput -notcontains 'RELEASE_ARTIFACT_PRIVACY_PASS') {
            throw 'The release privacy scanner rejected a valid required deny configuration.'
        }

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
