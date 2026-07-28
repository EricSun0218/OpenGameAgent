param(
    [string]$Archive = (
        Join-Path (Split-Path -Parent $PSScriptRoot) (
            "artifacts\game-agent-runtime-godot-0.1.0-test.zip")),
    [string]$Godot = "godot"
)

$ErrorActionPreference = "Stop"
$godotRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $godotRoot "..\.."))
$templateRoot = Join-Path $repositoryRoot "tests\godot\package-consumer-template"
$artifactsRoot = [System.IO.Path]::GetFullPath(
    (Join-Path $godotRoot "artifacts"))
$consumerRoot = Join-Path $artifactsRoot (
    "consumer-" + [System.Guid]::NewGuid().ToString("N"))
$packageRoot = Join-Path $artifactsRoot (
    "package-" + [System.Guid]::NewGuid().ToString("N"))
. (Join-Path $PSScriptRoot "GodotProcess.ps1")

$resolved = Get-Command $Godot -ErrorAction Stop
$item = Get-Item -LiteralPath $resolved.Source
$godotExecutable = if ($item.LinkType -and $item.Target) {
    [string]$item.Target[0]
}
else {
    $resolved.Source
}

function Invoke-Godot {
    param([string[]]$Arguments)

    return Invoke-CheckedGodotProcess `
        -Executable $godotExecutable `
        -Arguments $Arguments `
        -TimeoutSeconds 300
}

$Archive = [IO.Path]::GetFullPath(
    (Resolve-Path -LiteralPath $Archive).Path)
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
                    $_ -eq '..'
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
}
finally {
    $packageArchive.Dispose()
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
    $archiveName = [IO.Path]::GetFileNameWithoutExtension($Archive)
    $archivePrefix = "game-agent-runtime-godot-"
    if (-not $archiveName.StartsWith(
            $archivePrefix,
            [StringComparison]::Ordinal)) {
        throw "Packaged addon archive has an invalid name."
    }
    $expectedVersion = $archiveName.Substring($archivePrefix.Length)
    $pluginManifest = Join-Path $consumerRoot `
        "addons\game_agent_runtime\plugin.cfg"
    $pluginText = Get-Content -LiteralPath $pluginManifest -Raw
    if ($pluginText -notmatch '(?m)^version="([^"]+)"$' `
        -or $Matches[1] -ne $expectedVersion) {
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
    if ($buildExitCode -ne 0) {
        throw "Packaged addon build failed with exit code $buildExitCode."
    }

    $runExitCode = Invoke-Godot @(
        "--headless",
        "--path",
        $consumerRoot,
        "--quit-after",
        "100000")
    if ($runExitCode -ne 0) {
        throw "Packaged addon headless smoke failed with exit code $runExitCode."
    }
    if ($script:LastGodotOutput -notmatch "PACKAGED_CONSUMER_PASS") {
        throw "Packaged addon smoke exited without the PACKAGED_CONSUMER_PASS marker."
    }

    & (Join-Path $repositoryRoot `
        "engines\shared\Test-ReleaseArtifactPrivacy.ps1") `
        -Path $Archive

    Write-Output "PACKAGED_GODOT_ADDON_PASS"
}
finally {
    foreach ($temporaryPath in @($consumerRoot, $packageRoot)) {
        $resolvedTemporary = [System.IO.Path]::GetFullPath($temporaryPath)
        $isUnderArtifacts = $resolvedTemporary.StartsWith(
            $artifactsRoot + [System.IO.Path]::DirectorySeparatorChar,
            [System.StringComparison]::OrdinalIgnoreCase)
        if ($isUnderArtifacts -and
            (Test-Path -LiteralPath $resolvedTemporary)) {
            Remove-Item -LiteralPath $resolvedTemporary -Recurse -Force
        }
    }
}
