param(
    [string]$Configuration = "Release",
    [string]$Version = "0.1.0-alpha.1"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$pathComparison = if (
    [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
) {
    [StringComparison]::OrdinalIgnoreCase
}
else {
    [StringComparison]::Ordinal
}

function Test-PathWithin {
    param(
        [Parameter(Mandatory)]
        [string]$Path,

        [Parameter(Mandatory)]
        [string]$Parent
    )

    $resolvedPath = [IO.Path]::GetFullPath($Path)
    $resolvedParent = [IO.Path]::GetFullPath($Parent).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
    $requiredPrefix = $resolvedParent + [IO.Path]::DirectorySeparatorChar
    return $resolvedPath.StartsWith(
        $requiredPrefix,
        $script:pathComparison)
}

function Assert-NoFileSystemLinks {
    param(
        [Parameter(Mandatory)]
        [string]$Root
    )

    $rootItem = Get-Item -LiteralPath $Root
    if (($rootItem.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "Package sources must not be filesystem links."
    }

    foreach ($item in Get-ChildItem -LiteralPath $Root -Recurse -Force) {
        if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "Package sources must not contain filesystem links."
        }
    }
}

function New-CanonicalZipArchive {
    param(
        [Parameter(Mandatory)]
        [string]$SourceRoot,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.IO.Compression.FileSystem

    $resolvedSource = [IO.Path]::GetFullPath($SourceRoot)
    $resolvedDestination = [IO.Path]::GetFullPath($Destination)
    if (-not (Test-Path -LiteralPath $resolvedSource -PathType Container)) {
        throw "Package staging directory does not exist."
    }
    $sourcePrefix = $resolvedSource.TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar)) +
        [IO.Path]::DirectorySeparatorChar

    Assert-NoFileSystemLinks -Root $resolvedSource
    $items = @(
        Get-ChildItem -LiteralPath $resolvedSource -Recurse -Force |
            Where-Object { -not $_.PSIsContainer } |
            Sort-Object FullName
    )
    if ($items.Count -eq 0) {
        throw "Package staging directory is empty."
    }

    $entryNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::Ordinal)
    $outputStream = $null
    $archive = $null
    $archiveCompleted = $false
    try {
        $outputStream = [IO.File]::Open(
            $resolvedDestination,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None)
        $archive = [IO.Compression.ZipArchive]::new(
            $outputStream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true)

        foreach ($item in $items) {
            $resolvedItem = [IO.Path]::GetFullPath($item.FullName)
            if (-not (Test-PathWithin `
                    -Path $resolvedItem `
                    -Parent $resolvedSource)) {
                throw "Package file escaped the staging directory."
            }

            $entryName = $resolvedItem.Substring($sourcePrefix.Length)
            $entryName = $entryName.Replace('\', '/')
            $segments = @(
                $entryName.Split('/') |
                    Where-Object { $_.Length -gt 0 }
            )
            if ([string]::IsNullOrWhiteSpace($entryName) `
                -or $entryName.Contains('\') `
                -or $entryName.StartsWith(
                    '/',
                    [StringComparison]::Ordinal) `
                -or $segments -contains '..') {
                throw "Package archive entry has an unsafe name."
            }
            if (-not $entryNames.Add($entryName)) {
                throw "Package archive contains a duplicate entry."
            }

            $entry = $archive.CreateEntry(
                $entryName,
                [IO.Compression.CompressionLevel]::Optimal)
            $entry.LastWriteTime = [DateTimeOffset]::new(
                1980,
                1,
                1,
                0,
                0,
                0,
                [TimeSpan]::Zero)
            $sourceStream = $null
            $entryStream = $null
            try {
                $sourceStream = [IO.File]::OpenRead($resolvedItem)
                $entryStream = $entry.Open()
                $sourceStream.CopyTo($entryStream)
            }
            finally {
                if ($null -ne $entryStream) {
                    $entryStream.Dispose()
                }
                if ($null -ne $sourceStream) {
                    $sourceStream.Dispose()
                }
            }
        }
        $archiveCompleted = $true
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        if ($null -ne $outputStream) {
            $outputStream.Dispose()
        }
        if (-not $archiveCompleted `
            -and (Test-Path -LiteralPath $resolvedDestination)) {
            Remove-Item -LiteralPath $resolvedDestination -Force
        }
    }
}

function Publish-FileAtomically {
    param(
        [Parameter(Mandatory)]
        [string]$StagedPath,

        [Parameter(Mandatory)]
        [string]$Destination
    )

    $resolvedStaged = [IO.Path]::GetFullPath($StagedPath)
    $resolvedDestination = [IO.Path]::GetFullPath($Destination)
    if (-not (Test-Path -LiteralPath $resolvedStaged -PathType Leaf)) {
        throw "The staged package archive does not exist."
    }
    if (-not [string]::Equals(
            [IO.Path]::GetDirectoryName($resolvedStaged),
            [IO.Path]::GetDirectoryName($resolvedDestination),
            $script:pathComparison)) {
        throw "The staged and published archives must be on the same directory."
    }

    if (Test-Path -LiteralPath $resolvedDestination) {
        $destinationItem = Get-Item -LiteralPath $resolvedDestination
        if ($destinationItem.PSIsContainer `
            -or ($destinationItem.Attributes `
                -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw "The published archive must be a regular file."
        }
        $backup = $resolvedDestination `
            + "." `
            + [Guid]::NewGuid().ToString("N") `
            + ".previous"
        [IO.File]::Replace(
            $resolvedStaged,
            $resolvedDestination,
            $backup)
        try {
            Remove-Item -LiteralPath $backup -Force
        }
        catch {
            Write-Warning "The previous Godot package backup could not be removed: '$backup'."
        }
    }
    else {
        [IO.File]::Move($resolvedStaged, $resolvedDestination)
    }
}

$godotRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot ".."))
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $godotRoot "..\.."))
$artifactsRoot = [IO.Path]::GetFullPath(
    (Join-Path $godotRoot "artifacts"))
$systemTemporaryRoot = [IO.Path]::GetFullPath(
    [IO.Path]::GetTempPath()).TrimEnd(
    [char[]]@(
        [IO.Path]::DirectorySeparatorChar,
        [IO.Path]::AltDirectorySeparatorChar))
$stagingRoot = [IO.Path]::GetFullPath(
    (Join-Path $systemTemporaryRoot (
        "gar-gd-" + [Guid]::NewGuid().ToString("N"))))
$packageRoot = [IO.Path]::GetFullPath(
    (Join-Path $stagingRoot "pkg"))
$addonSource = [IO.Path]::GetFullPath(
    (Join-Path $godotRoot "addons\game_agent_runtime"))
$addonTarget = [IO.Path]::GetFullPath(
    (Join-Path $packageRoot "addons\game_agent_runtime"))
$libraryTarget = [IO.Path]::GetFullPath(
    (Join-Path $addonTarget "lib\netstandard2.1"))
$archivePath = [IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot "game-agent-runtime-godot-$Version.zip"))
$stagedArchivePath = [IO.Path]::GetFullPath(
    (Join-Path $artifactsRoot (
        ".game-agent-runtime-godot-$Version." +
        [Guid]::NewGuid().ToString("N") +
        ".tmp")))

if ($Version -notmatch "^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?$") {
    throw "Version must be a filename-safe semantic version."
}
if (-not (Test-PathWithin -Path $godotRoot -Parent $repositoryRoot)) {
    throw "Godot workspace must be inside the repository."
}
if (-not (Test-PathWithin -Path $artifactsRoot -Parent $godotRoot)) {
    throw "Artifacts directory must be inside the Godot workspace."
}
if (-not (Test-PathWithin -Path $addonSource -Parent $godotRoot)) {
    throw "Addon source must be inside the Godot workspace."
}
if (-not (Test-PathWithin -Path $stagingRoot -Parent $systemTemporaryRoot)) {
    throw "Staging directory must be inside the system temporary directory."
}
if (-not (Test-PathWithin -Path $packageRoot -Parent $stagingRoot) `
    -or -not (Test-PathWithin -Path $addonTarget -Parent $packageRoot) `
    -or -not (Test-PathWithin -Path $libraryTarget -Parent $addonTarget)) {
    throw "Package staging paths are invalid."
}
if (-not (Test-PathWithin -Path $archivePath -Parent $artifactsRoot)) {
    throw "Archive must be inside the Godot artifacts directory."
}
if (-not (Test-PathWithin -Path $stagedArchivePath -Parent $artifactsRoot)) {
    throw "Staged archive must be inside the Godot artifacts directory."
}
if (-not (Test-Path -LiteralPath $addonSource -PathType Container)) {
    throw "Godot addon source directory does not exist."
}
Assert-NoFileSystemLinks -Root $addonSource

try {
    New-Item -ItemType Directory -Path $libraryTarget -Force | Out-Null

    & dotnet build (
        Join-Path $repositoryRoot "src\GameAgent.Protocol\GameAgent.Protocol.csproj"
    ) -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "GameAgent.Protocol build failed."
    }

    & dotnet build (
        Join-Path $repositoryRoot "src\GameAgent.Core\GameAgent.Core.csproj"
    ) -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "GameAgent.Core build failed."
    }

    & dotnet build (
        Join-Path $repositoryRoot "src\GameAgent.Persistence\GameAgent.Persistence.csproj"
    ) -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "GameAgent.Persistence build failed."
    }

    & dotnet build (
        Join-Path $repositoryRoot "src\GameAgent.Generation\GameAgent.Generation.csproj"
    ) -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "GameAgent.Generation build failed."
    }

    & dotnet build (
        Join-Path $repositoryRoot (
            "src\GameAgent.Providers.Anthropic\" +
            "GameAgent.Providers.Anthropic.csproj")
    ) -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "GameAgent.Providers.Anthropic build failed."
    }

    & dotnet build (
        Join-Path $repositoryRoot (
            "src\GameAgent.Providers.OpenAICompatible\" +
            "GameAgent.Providers.OpenAICompatible.csproj")
    ) -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "GameAgent.Providers.OpenAICompatible build failed."
    }

    & dotnet build (
        Join-Path $repositoryRoot (
            "src\GameAgent.Providers.MediaHttp\" +
            "GameAgent.Providers.MediaHttp.csproj")
    ) -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "GameAgent.Providers.MediaHttp build failed."
    }

    & dotnet build (
        Join-Path $repositoryRoot "src\GameAgent.Runtime\GameAgent.Runtime.csproj"
    ) -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "GameAgent.Runtime build failed."
    }

    & dotnet build (
        Join-Path $repositoryRoot (
            "src\GameAgent.Workflow\GameAgent.Workflow.csproj")
    ) -c $Configuration
    if ($LASTEXITCODE -ne 0) {
        throw "GameAgent.Workflow build failed."
    }

    Get-ChildItem -LiteralPath $addonSource -Force |
        Copy-Item -Destination $addonTarget -Recurse -Force
    $pluginManifest = Join-Path $addonTarget "plugin.cfg"
    $pluginText = [IO.File]::ReadAllText($pluginManifest)
    $versionPattern =
        '(?m)^version="(?<version>[^"\r\n]+)"(?<cr>\r?)$'
    $versionMatches = [regex]::Matches($pluginText, $versionPattern)
    if ($versionMatches.Count -ne 1) {
        throw "The staged Godot plugin manifest must contain one version field."
    }
    $versionMatch = $versionMatches[0]
    $versionReplacement =
        'version="' + $Version + '"' + $versionMatch.Groups['cr'].Value
    $pluginText = $pluginText.Remove(
        $versionMatch.Index,
        $versionMatch.Length).Insert(
            $versionMatch.Index,
            $versionReplacement)
    [IO.File]::WriteAllText(
        $pluginManifest,
        $pluginText,
        [Text.UTF8Encoding]::new($false))
    Copy-Item -LiteralPath (Join-Path $repositoryRoot "LICENSE") `
        -Destination (Join-Path $packageRoot "LICENSE") -Force
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot (
            "src\GameAgent.Protocol\bin\$Configuration\netstandard2.1\GameAgent.Protocol.dll")
    ) -Destination $libraryTarget
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot (
            "src\GameAgent.Core\bin\$Configuration\netstandard2.1\GameAgent.Core.dll")
    ) -Destination $libraryTarget
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot (
            "src\GameAgent.Persistence\bin\$Configuration\netstandard2.1\GameAgent.Persistence.dll")
    ) -Destination $libraryTarget
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot (
            "src\GameAgent.Generation\bin\$Configuration\netstandard2.1\GameAgent.Generation.dll")
    ) -Destination $libraryTarget
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot (
            "src\GameAgent.Providers.Anthropic\bin\$Configuration\" +
            "netstandard2.1\GameAgent.Providers.Anthropic.dll")
    ) -Destination $libraryTarget
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot (
            "src\GameAgent.Providers.OpenAICompatible\bin\$Configuration\" +
            "netstandard2.1\GameAgent.Providers.OpenAICompatible.dll")
    ) -Destination $libraryTarget
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot (
            "src\GameAgent.Providers.MediaHttp\bin\$Configuration\" +
            "netstandard2.1\GameAgent.Providers.MediaHttp.dll")
    ) -Destination $libraryTarget
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot (
            "src\GameAgent.Runtime\bin\$Configuration\netstandard2.1\GameAgent.Runtime.dll")
    ) -Destination $libraryTarget
    Copy-Item -LiteralPath (
        Join-Path $repositoryRoot (
            "src\GameAgent.Workflow\bin\$Configuration\netstandard2.1\GameAgent.Workflow.dll")
    ) -Destination $libraryTarget
    New-Item -ItemType Directory -Path $artifactsRoot -Force | Out-Null
    $artifactsItem = Get-Item -LiteralPath $artifactsRoot
    if (($artifactsItem.Attributes `
            -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw "The Godot artifacts directory must not be a filesystem link."
    }
    New-CanonicalZipArchive `
        -SourceRoot $packageRoot `
        -Destination $stagedArchivePath
    Publish-FileAtomically `
        -StagedPath $stagedArchivePath `
        -Destination $archivePath
    Write-Output $archivePath
}
finally {
    if ((Test-PathWithin `
            -Path $stagedArchivePath `
            -Parent $artifactsRoot) `
        -and (Test-Path -LiteralPath $stagedArchivePath -PathType Leaf)) {
        Remove-Item -LiteralPath $stagedArchivePath -Force
    }
    $resolvedStaging = [IO.Path]::GetFullPath($stagingRoot)
    $isExpectedStagingDirectory = (
        Test-PathWithin `
            -Path $resolvedStaging `
            -Parent $systemTemporaryRoot
    ) -and [IO.Path]::GetFileName($resolvedStaging).StartsWith(
        "gar-gd-",
        [StringComparison]::Ordinal)
    if ($isExpectedStagingDirectory `
        -and (Test-Path -LiteralPath $resolvedStaging)) {
        Remove-Item -LiteralPath $resolvedStaging -Recurse -Force
    }
}
