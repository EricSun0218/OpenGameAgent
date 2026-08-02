param(
    [string]$Archive = (
        Join-Path (Split-Path -Parent $PSScriptRoot) (
            "artifacts\game-agent-runtime-godot-0.1.0-alpha.1.zip")),
    [string]$Godot = "godot",
    [string]$ExpectedVersion
)

$ErrorActionPreference = "Stop"
$godotRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $godotRoot "..\.."))
$templateRoot = Join-Path $repositoryRoot "tests\godot\package-consumer-template"
$systemTemporaryRoot = [System.IO.Path]::GetFullPath(
    [System.IO.Path]::GetTempPath()).TrimEnd(
        [System.IO.Path]::DirectorySeparatorChar,
        [System.IO.Path]::AltDirectorySeparatorChar)
$verificationRoot = Join-Path $systemTemporaryRoot (
    "gar-godot-consumer-" + [System.Guid]::NewGuid().ToString("N"))
$consumerRoot = Join-Path $verificationRoot "consumer"
$packageRoot = Join-Path $verificationRoot "package"
. (Join-Path $PSScriptRoot "GodotProcess.ps1")

function Invoke-Godot {
    param([string[]]$Arguments)

    return Invoke-CheckedGodotProcess `
        -Executable $godotExecutable `
        -Arguments $Arguments `
        -TimeoutSeconds 300
}

function Test-WindowsPortablePathSegment {
    param([Parameter(Mandatory)][string]$Segment)

    if ([string]::IsNullOrEmpty($Segment) `
        -or $Segment.Length -gt 255 `
        -or $Segment -match '[<>:"/\\|?*\x00-\x1f]' `
        -or $Segment -match '[. ]$') {
        return $false
    }

    $stem = $Segment.Split('.')[0].TrimEnd([char[]]@(' ', '.'))
    return $stem -notmatch (
        '^(?i:CON|PRN|AUX|NUL|CLOCK[$]|CONIN[$]|CONOUT[$]|' +
        'COM[0-9¹²³]|LPT[0-9¹²³])$')
}

if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) `
    -and $ExpectedVersion -notmatch (
        '^[0-9]+\.[0-9]+\.[0-9]+' +
        '(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')) {
    throw "The expected Godot package version is invalid."
}

$Archive = [IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath $Archive).Path)
$archiveName = [IO.Path]::GetFileNameWithoutExtension($Archive)
$archivePrefix = "game-agent-runtime-godot-"
if (-not $archiveName.StartsWith(
        $archivePrefix,
        [StringComparison]::Ordinal)) {
    throw "Packaged addon archive has an invalid name."
}
$archiveVersion = $archiveName.Substring($archivePrefix.Length)
if ($archiveVersion -notmatch (
        '^[0-9]+\.[0-9]+\.[0-9]+' +
        '(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')) {
    throw "Packaged addon archive has an invalid version."
}
if (-not [string]::IsNullOrWhiteSpace($ExpectedVersion) `
    -and -not [string]::Equals(
        $archiveVersion,
        $ExpectedVersion,
        [StringComparison]::Ordinal)) {
    throw "Packaged addon archive does not match the expected release version."
}

Add-Type -AssemblyName System.IO.Compression.FileSystem
$packageArchive = [IO.Compression.ZipFile]::OpenRead($Archive)
try {
    if ($packageArchive.Entries.Count -eq 0) {
        throw "Packaged addon archive is empty."
    }

    $entryNames = [Collections.Generic.HashSet[string]]::new(
        [StringComparer]::OrdinalIgnoreCase)
    foreach ($entry in $packageArchive.Entries) {
        $entryName = $entry.FullName
        $segments = @($entryName.Split('/'))
        if ([string]::IsNullOrWhiteSpace($entryName) `
            -or $entryName.Contains('\') `
            -or $entryName.StartsWith(
                '/',
                [StringComparison]::Ordinal) `
            -or $entryName -match '^[A-Za-z]:' `
            -or @($segments | Where-Object {
                    [string]::IsNullOrEmpty($_) -or
                    $_ -eq '.' -or
                    $_ -eq '..' -or
                    -not (Test-WindowsPortablePathSegment -Segment $_)
                }).Count -ne 0 `
            -or (-not [string]::Equals(
                    $entryName,
                    'LICENSE',
                    [StringComparison]::Ordinal) -and
                -not $entryName.StartsWith(
                    'addons/game_agent_runtime/',
                    [StringComparison]::Ordinal))) {
            throw "Packaged addon archive contains a non-canonical entry name."
        }
        if (-not $entryNames.Add($entryName)) {
            throw "Packaged addon archive contains duplicate entries."
        }
    }

    $libraryPrefix =
        'addons/game_agent_runtime/lib/netstandard2.1/'
    $expectedLibraryEntries = @(
        'GameAgent.Core.dll',
        'GameAgent.Generation.dll',
        'GameAgent.Persistence.dll',
        'GameAgent.Protocol.dll',
        'GameAgent.Providers.Anthropic.dll',
        'GameAgent.Providers.MediaHttp.dll',
        'GameAgent.Providers.OpenAICompatible.dll',
        'GameAgent.Runtime.dll',
        'GameAgent.Workflow.dll'
    ) | ForEach-Object { $libraryPrefix + $_ }
    $actualLibraryEntries = @(
        $packageArchive.Entries |
            Where-Object {
                [string]::Equals(
                    [IO.Path]::GetExtension($_.FullName),
                    '.dll',
                    [StringComparison]::OrdinalIgnoreCase)
            } |
            ForEach-Object { $_.FullName } |
            Sort-Object
    )
    if ([string]::Join("`n", $actualLibraryEntries) -cne
        [string]::Join(
            "`n",
            @($expectedLibraryEntries | Sort-Object))) {
        throw "Packaged addon managed-library set is incomplete or unexpected."
    }

    $symbolEntries = @(
        $packageArchive.Entries |
            Where-Object {
                [string]::Equals(
                    [IO.Path]::GetExtension($_.FullName),
                    '.pdb',
                    [StringComparison]::OrdinalIgnoreCase)
            })
    if ($symbolEntries.Count -ne 0) {
        throw "Packaged addon managed-symbol set is unexpected."
    }

    $unsupportedExecutableEntries = @(
        $packageArchive.Entries |
            Where-Object {
                $_.FullName -match
                    '(?i)(?:[.]exe|[.]dylib|[.]bundle|[.]so(?:[.][0-9]+)*)$'
            })
    if ($unsupportedExecutableEntries.Count -ne 0) {
        throw "Packaged addon contains an unsupported executable binary."
    }

}
finally {
    $packageArchive.Dispose()
}

$resolved = Get-Command $Godot -ErrorAction Stop
$item = Get-Item -LiteralPath $resolved.Source
$godotExecutable = if ($item.LinkType -and $item.Target) {
    $targets = @($item.Target)
    $target = [string]$targets[0]
    if ([System.IO.Path]::IsPathRooted($target)) {
        $target
    }
    else {
        Join-Path $item.DirectoryName $target
    }
}
else {
    $resolved.Source
}

New-Item `
    -ItemType Directory `
    -Path $consumerRoot, $packageRoot `
    -Force |
    Out-Null

try {
    Copy-Item -Path (Join-Path $templateRoot "*") -Destination $consumerRoot -Recurse
    Expand-Archive -LiteralPath $Archive -DestinationPath $packageRoot
    Copy-Item `
        -LiteralPath (Join-Path $packageRoot 'addons') `
        -Destination (Join-Path $consumerRoot 'addons') `
        -Recurse
    Copy-Item `
        -LiteralPath (Join-Path $packageRoot 'LICENSE') `
        -Destination (Join-Path $consumerRoot 'LICENSE')
    $pluginManifest = Join-Path $consumerRoot `
        "addons\game_agent_runtime\plugin.cfg"
    $pluginText = Get-Content -LiteralPath $pluginManifest -Raw
    $versionPattern =
        '(?m)^version="(?<version>[^"\r\n]+)"\r?$'
    $versionMatches = [regex]::Matches($pluginText, $versionPattern)
    if ($versionMatches.Count -ne 1 `
        -or $versionMatches[0].Groups['version'].Value -cne $archiveVersion) {
        throw "Packaged addon version does not match its archive name."
    }
    $licensePath = Join-Path $consumerRoot "LICENSE"
    if (-not (Test-Path -LiteralPath $licensePath -PathType Leaf)) {
        throw "Packaged addon is missing LICENSE."
    }
    $licenseText = Get-Content -LiteralPath $licensePath -Raw
    if ($licenseText -notmatch "Apache License" `
        -or $licenseText -notmatch "TERMS AND CONDITIONS") {
        throw "Packaged addon does not contain the complete license."
    }

    $buildExitCode = Invoke-Godot @(
        "--headless",
        "--path",
        $consumerRoot,
        "--build-solutions",
        "--quit-after",
        "10")
    $buildOutput = $script:LastGodotOutput
    if ($buildExitCode -ne 0) {
        throw "Packaged addon build failed with exit code $buildExitCode."
    }

    $editorExitCode = Invoke-Godot @(
        "--headless",
        "--editor",
        "--path",
        $consumerRoot,
        "--quit-after",
        "30")
    $editorOutput = $script:LastGodotOutput
    if ($editorExitCode -ne 0) {
        throw "Packaged addon editor-plugin load failed with exit code $editorExitCode."
    }
    if (($buildOutput + "`n" + $editorOutput) -match
        '(?im)(^ERROR:|SCRIPT ERROR:|Parse Error:|PACKAGED_[A-Z_]*FAIL|Failed to (load|instantiate)|Cannot load)') {
        throw "Packaged addon editor-plugin load emitted an error."
    }

    $runExitCode = Invoke-Godot @(
        "--headless",
        "--path",
        $consumerRoot,
        "--quit-after",
        "100000")
    $runOutput = $script:LastGodotOutput
    if ($runExitCode -ne 0) {
        throw "Packaged addon headless smoke failed with exit code $runExitCode."
    }
    if ($runOutput -notmatch "PACKAGED_CONSUMER_PASS") {
        throw "Packaged addon smoke exited without the PACKAGED_CONSUMER_PASS marker."
    }
    if ($runOutput -match
        '(?im)(^ERROR:|SCRIPT ERROR:|Parse Error:|PACKAGED_[A-Z_]*FAIL|Failed to (load|instantiate)|Cannot load)') {
        throw "Packaged addon headless smoke emitted an error."
    }

    & (Join-Path $repositoryRoot `
        "engines\shared\Test-ReleaseArtifactPrivacy.ps1") `
        -Path $Archive

    Write-Output "PACKAGED_GODOT_ADDON_PASS"
}
finally {
    $resolvedVerificationRoot = [System.IO.Path]::GetFullPath(
        $verificationRoot).TrimEnd(
            [System.IO.Path]::DirectorySeparatorChar,
            [System.IO.Path]::AltDirectorySeparatorChar)
    $verificationParent = [System.IO.Path]::GetDirectoryName(
        $resolvedVerificationRoot)
    $verificationName = [System.IO.Path]::GetFileName(
        $resolvedVerificationRoot)
    $hasExpectedParent = [string]::Equals(
        $verificationParent,
        $systemTemporaryRoot,
        [System.StringComparison]::OrdinalIgnoreCase)
    $hasExpectedName = $verificationName.StartsWith(
        "gar-godot-consumer-",
        [System.StringComparison]::Ordinal)
    if ($hasExpectedParent -and $hasExpectedName -and
        (Test-Path -LiteralPath $resolvedVerificationRoot)) {
        Remove-Item `
            -LiteralPath $resolvedVerificationRoot `
            -Recurse `
            -Force
    }
}
