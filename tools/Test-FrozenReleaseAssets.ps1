[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $CandidateDirectory,
    [Parameter(Mandatory = $true)]
    [string] $FrozenDirectory
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-ArchivePayloadMap {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $ArchivePath,
        [switch] $IgnoreNuGetContainerMetadata
    )

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $result = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        if ($archive.Entries.Count -gt 10000) {
            throw "Archive '$ArchivePath' contains too many entries."
        }
        [long] $totalLength = 0
        [int] $ignoredRelationships = 0
        [int] $ignoredCoreProperties = 0
        foreach ($entry in $archive.Entries) {
            $name = [string]$entry.FullName
            if ([string]::IsNullOrWhiteSpace($name) -or
                $name.Contains('\') -or
                $name.Contains("`0") -or
                [IO.Path]::IsPathRooted($name) -or
                $name.Split('/', [StringSplitOptions]::RemoveEmptyEntries) -contains '..') {
                throw "Archive '$ArchivePath' contains unsafe entry '$name'."
            }
            if ($name.EndsWith('/', [StringComparison]::Ordinal)) {
                continue
            }
            if ($entry.Length -lt 0 -or $entry.Length -gt 314572800) {
                throw "Archive '$ArchivePath' entry '$name' exceeds its size bound."
            }
            $totalLength += $entry.Length
            if ($totalLength -gt 1073741824) {
                throw "Archive '$ArchivePath' exceeds its total uncompressed size bound."
            }

            if ($IgnoreNuGetContainerMetadata -and $name -ieq '_rels/.rels') {
                $ignoredRelationships++
                continue
            }
            if ($IgnoreNuGetContainerMetadata -and
                $name -imatch '^package/services/metadata/core-properties/[0-9a-f-]+\.psmdcp$') {
                $ignoredCoreProperties++
                continue
            }
            if ($name -ieq '.signature.p7s') {
                throw "Frozen release package '$ArchivePath' must be unsigned."
            }
            if ($result.ContainsKey($name)) {
                throw "Archive '$ArchivePath' contains duplicate entry '$name'."
            }

            $stream = $entry.Open()
            try {
                $sha256 = [Security.Cryptography.SHA256]::Create()
                try {
                    $hash = [Convert]::ToHexString($sha256.ComputeHash($stream))
                }
                finally {
                    $sha256.Dispose()
                }
            }
            finally {
                $stream.Dispose()
            }
            $result.Add($name, "$($entry.Length):$hash")
        }
        if ($IgnoreNuGetContainerMetadata -and
            ($ignoredRelationships -ne 1 -or $ignoredCoreProperties -ne 1)) {
            throw "NuGet package '$ArchivePath' does not contain exactly one expected OPC metadata pair."
        }
    }
    finally {
        $archive.Dispose()
    }

    return $result
}

function Assert-ArchivePayloadEqual {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $CandidatePath,
        [Parameter(Mandatory = $true)]
        [string] $FrozenPath,
        [switch] $IgnoreNuGetContainerMetadata
    )

    $candidate = Get-ArchivePayloadMap `
        -ArchivePath $CandidatePath `
        -IgnoreNuGetContainerMetadata:$IgnoreNuGetContainerMetadata
    $frozen = Get-ArchivePayloadMap `
        -ArchivePath $FrozenPath `
        -IgnoreNuGetContainerMetadata:$IgnoreNuGetContainerMetadata
    if ($candidate.Count -ne $frozen.Count) {
        throw "Frozen asset '$([IO.Path]::GetFileName($FrozenPath))' has a different payload entry count."
    }
    foreach ($entryName in $candidate.Keys) {
        if (-not $frozen.ContainsKey($entryName) -or
            -not [string]::Equals($candidate[$entryName], $frozen[$entryName], [StringComparison]::Ordinal)) {
            throw "Frozen asset '$([IO.Path]::GetFileName($FrozenPath))' differs at payload entry '$entryName'."
        }
    }
}

$candidateRoot = [IO.Path]::GetFullPath($CandidateDirectory)
$frozenRoot = [IO.Path]::GetFullPath($FrozenDirectory)
foreach ($root in @($candidateRoot, $frozenRoot)) {
    if (-not (Test-Path -LiteralPath $root -PathType Container)) {
        throw "Release asset directory '$root' does not exist."
    }
}

$candidateAssets = @{}
foreach ($asset in Get-ChildItem -LiteralPath $candidateRoot -File) {
    if ($asset.Name -in @('RELEASE_NOTES.md', 'SHA256SUMS.txt')) {
        continue
    }
    $candidateAssets[$asset.Name] = $asset.FullName
}
$frozenAssets = @{}
foreach ($asset in Get-ChildItem -LiteralPath $frozenRoot -File) {
    if ($asset.Name -in @('RELEASE_NOTES.md', 'SHA256SUMS.txt')) {
        continue
    }
    $frozenAssets[$asset.Name] = $asset.FullName
}
if ($candidateAssets.Count -ne $frozenAssets.Count) {
    throw 'Frozen release asset count differs from the trusted build candidate.'
}

foreach ($assetName in $candidateAssets.Keys) {
    if (-not $frozenAssets.ContainsKey($assetName)) {
        throw "Frozen release omits trusted asset '$assetName'."
    }
    $extension = [IO.Path]::GetExtension($assetName)
    if ($extension -ieq '.nupkg') {
        Assert-ArchivePayloadEqual `
            -CandidatePath $candidateAssets[$assetName] `
            -FrozenPath $frozenAssets[$assetName] `
            -IgnoreNuGetContainerMetadata
    }
    elseif ($extension -ieq '.zip') {
        Assert-ArchivePayloadEqual `
            -CandidatePath $candidateAssets[$assetName] `
            -FrozenPath $frozenAssets[$assetName]
    }
    else {
        $candidateHash = (Get-FileHash -LiteralPath $candidateAssets[$assetName] -Algorithm SHA256).Hash
        $frozenHash = (Get-FileHash -LiteralPath $frozenAssets[$assetName] -Algorithm SHA256).Hash
        if (-not [string]::Equals($candidateHash, $frozenHash, [StringComparison]::Ordinal)) {
            throw "Frozen release asset '$assetName' differs from the trusted build candidate."
        }
    }
}

Write-Output "Frozen release payload matches all $($candidateAssets.Count) trusted build assets."
