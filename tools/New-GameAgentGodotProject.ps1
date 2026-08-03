[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$Destination,

    [string]$ProjectName = "GameAgentStarter",

    [string]$PackageArchive
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

function Assert-RegularDirectory {
    param([Parameter(Mandatory)][string]$Path)

    $item = Get-Item -LiteralPath $Path
    if (-not $item.PSIsContainer `
        -or ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Expected a regular directory: '$Path'."
    }
}

function Copy-RegularTree {
    param(
        [Parameter(Mandatory)][string]$Source,
        [Parameter(Mandatory)][string]$Target
    )

    Assert-RegularDirectory -Path $Source
    foreach ($item in Get-ChildItem -LiteralPath $Source -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Starter sources must not contain filesystem links."
        }
    }

    New-Item -ItemType Directory -Path $Target | Out-Null
    Get-ChildItem -LiteralPath $Source -Force |
        Copy-Item -Destination $Target -Recurse -Force
}

function Assert-SafeArchiveSegment {
    param([Parameter(Mandatory)][string]$Segment)

    if ([string]::IsNullOrWhiteSpace($Segment) `
        -or $Segment.Length -gt 255 `
        -or $Segment -match '[<>:"|?*\x00-\x1F]' `
        -or $Segment.EndsWith('.') `
        -or $Segment.EndsWith(' ')) {
        throw "The addon package contains an unsafe entry name."
    }

    $deviceBase = $Segment.Split('.')[0]
    if ($deviceBase -match '^(?i:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])$') {
        throw "The addon package contains an unsafe entry name."
    }
}

function Expand-BoundedZip {
    param(
        [Parameter(Mandatory)][string]$ArchivePath,
        [Parameter(Mandatory)][string]$TargetRoot
    )

    $maximumEntries = 512
    $maximumEntryBytes = 32L * 1024 * 1024
    $maximumTotalBytes = 128L * 1024 * 1024
    $resolvedTarget = [IO.Path]::GetFullPath($TargetRoot).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
    $targetPrefix = $resolvedTarget + [IO.Path]::DirectorySeparatorChar
    $comparison = if (
        [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    $nameComparer = if (
        [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT) {
        [StringComparer]::OrdinalIgnoreCase
    }
    else {
        [StringComparer]::Ordinal
    }
    $names = [Collections.Generic.HashSet[string]]::new($nameComparer)

    Add-Type -AssemblyName System.IO.Compression
    $source = [IO.File]::OpenRead($ArchivePath)
    $zip = $null
    try {
        $zip = [IO.Compression.ZipArchive]::new(
            $source,
            [IO.Compression.ZipArchiveMode]::Read,
            $false)
        if ($zip.Entries.Count -gt $maximumEntries) {
            throw "The addon package contains too many entries."
        }

        $declaredTotal = 0L
        $actualTotal = 0L
        foreach ($entry in $zip.Entries) {
            $name = $entry.FullName
            $segments = @($name.Split('/') | Where-Object {
                -not [string]::IsNullOrEmpty($_)
            })
            if ([string]::IsNullOrWhiteSpace($name) `
                -or $name.Length -gt 512 `
                -or $name.Contains('\') `
                -or $name.StartsWith('/') `
                -or [IO.Path]::IsPathRooted($name) `
                -or $segments -contains '.' `
                -or $segments -contains '..') {
                throw "The addon package contains an unsafe entry name."
            }
            foreach ($segment in $segments) {
                Assert-SafeArchiveSegment -Segment $segment
            }

            $unixFileType = ($entry.ExternalAttributes -shr 16) -band 0xF000
            if ($unixFileType -eq 0xA000) {
                throw "The addon package must not contain symbolic links."
            }

            $normalizedName = $name.TrimEnd('/')
            if ([string]::IsNullOrWhiteSpace($normalizedName) `
                -or -not $names.Add($normalizedName)) {
                throw "The addon package contains a duplicate entry."
            }

            $target = [IO.Path]::GetFullPath(
                (Join-Path $resolvedTarget $normalizedName))
            if (-not $target.StartsWith($targetPrefix, $comparison)) {
                throw "The addon package entry escapes the target directory."
            }

            if ($name.EndsWith('/')) {
                New-Item -ItemType Directory -Path $target -Force | Out-Null
                continue
            }

            if ($entry.Length -lt 0 -or $entry.Length -gt $maximumEntryBytes) {
                throw "The addon package contains an oversized entry."
            }
            if ($declaredTotal -gt $maximumTotalBytes - $entry.Length) {
                throw "The addon package exceeds the extraction byte bound."
            }
            $declaredTotal += $entry.Length

            $parent = [IO.Path]::GetDirectoryName($target)
            New-Item -ItemType Directory -Path $parent -Force | Out-Null
            $input = $entry.Open()
            $output = [IO.File]::Open(
                $target,
                [IO.FileMode]::CreateNew,
                [IO.FileAccess]::Write,
                [IO.FileShare]::None)
            try {
                $buffer = [byte[]]::new(65536)
                $entryBytes = 0L
                while (($read = $input.Read($buffer, 0, $buffer.Length)) -gt 0) {
                    if ($entryBytes -gt $maximumEntryBytes - $read `
                        -or $actualTotal -gt $maximumTotalBytes - $read) {
                        throw "The addon package exceeded its extraction bound."
                    }
                    $entryBytes += $read
                    $actualTotal += $read
                    $output.Write($buffer, 0, $read)
                }
                if ($entryBytes -ne $entry.Length) {
                    throw "The addon package entry length is inconsistent."
                }
            }
            finally {
                $output.Dispose()
                $input.Dispose()
            }
        }
    }
    finally {
        if ($null -ne $zip) {
            $zip.Dispose()
        }
        $source.Dispose()
    }
}

if ($ProjectName -notmatch '^[\p{L}][\p{L}\p{N} ._-]{0,63}$') {
    throw "ProjectName must start with a letter and contain at most 64 safe characters."
}

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$templateRoot = Join-Path $repositoryRoot "templates\godot-agent-starter"
$sampleRoot = Join-Path $repositoryRoot "engines\godot\samples\basic"
$livingWorldSampleRoot = Join-Path $repositoryRoot `
    "engines\godot\samples\living_world"
$packageScript = Join-Path $repositoryRoot `
    "engines\godot\tools\package-addon.ps1"
$resolvedDestination = [IO.Path]::GetFullPath($Destination)
$destinationLeaf = [IO.Path]::GetFileName(
    $resolvedDestination.TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)))
if ([string]::IsNullOrWhiteSpace($destinationLeaf)) {
    throw "Destination must name a project directory, not a filesystem root."
}

if (Test-Path -LiteralPath $resolvedDestination) {
    throw "Destination already exists; the scaffold never overwrites it."
}

$destinationParent = [IO.Path]::GetDirectoryName($resolvedDestination)
if ([string]::IsNullOrWhiteSpace($destinationParent)) {
    throw "Destination must have a parent directory."
}

if (-not (Test-Path -LiteralPath $destinationParent)) {
    New-Item -ItemType Directory -Path $destinationParent -Force | Out-Null
}
Assert-RegularDirectory -Path $destinationParent

if ([string]::IsNullOrWhiteSpace($PackageArchive)) {
    $packageOutput = & $packageScript -Configuration Release
    if ($LASTEXITCODE -ne 0) {
        throw "Godot addon packaging failed."
    }

    $PackageArchive = @($packageOutput)[-1]
}

$resolvedArchive = [IO.Path]::GetFullPath($PackageArchive)
if (-not (Test-Path -LiteralPath $resolvedArchive -PathType Leaf)) {
    throw "Godot addon package does not exist: '$resolvedArchive'."
}

$assemblyName = [regex]::Replace($ProjectName, '[^\p{L}\p{N}_]+', '.')
$assemblyName = $assemblyName.Trim('.')
if ([string]::IsNullOrWhiteSpace($assemblyName)) {
    throw "ProjectName could not produce a valid assembly name."
}

$stageName = ".gar-starter-" + [Guid]::NewGuid().ToString("N")
$stageRoot = Join-Path $destinationParent $stageName
$unpackRoot = Join-Path $stageRoot ".package"
$published = $false
try {
    New-Item -ItemType Directory -Path $unpackRoot | Out-Null
    Expand-BoundedZip `
        -ArchivePath $resolvedArchive `
        -TargetRoot $unpackRoot

    $unpackedAddon = Join-Path $unpackRoot "addons\game_agent_runtime"
    if (-not (Test-Path -LiteralPath $unpackedAddon -PathType Container)) {
        throw "The package does not contain addons/game_agent_runtime."
    }

    Copy-RegularTree `
        -Source $unpackedAddon `
        -Target (Join-Path $stageRoot "addons\game_agent_runtime")
    Copy-RegularTree `
        -Source $sampleRoot `
        -Target (Join-Path $stageRoot "samples\basic")
    Copy-RegularTree `
        -Source $livingWorldSampleRoot `
        -Target (Join-Path $stageRoot "samples\living_world")

    $projectTemplate = [IO.File]::ReadAllText(
        (Join-Path $templateRoot "project.godot.template"))
    $projectText = $projectTemplate.Replace(
        "__PROJECT_NAME__",
        $ProjectName).Replace(
            "__ASSEMBLY_NAME__",
            $assemblyName)
    [IO.File]::WriteAllText(
        (Join-Path $stageRoot "project.godot"),
        $projectText,
        [Text.UTF8Encoding]::new($false))

    $projectFileTemplate = [IO.File]::ReadAllText(
        (Join-Path $templateRoot "GameAgentStarter.csproj.template"))
    $projectFileText = $projectFileTemplate.Replace(
        "__ASSEMBLY_NAME__",
        $assemblyName)
    [IO.File]::WriteAllText(
        (Join-Path $stageRoot ($assemblyName + ".csproj")),
        $projectFileText,
        [Text.UTF8Encoding]::new($false))

    $readmeTemplate = [IO.File]::ReadAllText(
        (Join-Path $templateRoot "README.md.template"))
    [IO.File]::WriteAllText(
        (Join-Path $stageRoot "README.md"),
        $readmeTemplate.Replace("__PROJECT_NAME__", $ProjectName),
        [Text.UTF8Encoding]::new($false))
    [IO.File]::WriteAllText(
        (Join-Path $stageRoot ".gitignore"),
        ".godot/`nbin/`nobj/`n.env`n.env.*`n",
        [Text.UTF8Encoding]::new($false))

    Remove-Item -LiteralPath $unpackRoot -Recurse -Force
    [IO.Directory]::Move($stageRoot, $resolvedDestination)
    $published = $true
    Write-Output $resolvedDestination
}
finally {
    if (-not $published -and (Test-Path -LiteralPath $stageRoot)) {
        $resolvedStage = [IO.Path]::GetFullPath($stageRoot)
        $expectedPrefix = ([IO.Path]::GetFullPath(
            $destinationParent)).TrimEnd(
                [char[]]@(
                    [IO.Path]::DirectorySeparatorChar,
                    [IO.Path]::AltDirectorySeparatorChar)) `
            + [IO.Path]::DirectorySeparatorChar `
            + ".gar-starter-"
        if ($resolvedStage.StartsWith(
                $expectedPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedStage -Recurse -Force
        }
    }
}
