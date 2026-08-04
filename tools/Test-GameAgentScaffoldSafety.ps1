[CmdletBinding()]
param()

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$scaffoldScript = Join-Path $PSScriptRoot `
    "New-GameAgentGodotProject.ps1"
$systemTemporaryRoot = [IO.Path]::GetFullPath(
    [IO.Path]::GetTempPath()).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
$testRoot = Join-Path $systemTemporaryRoot `
    ("gar-scaffold-safety-" + [Guid]::NewGuid().ToString("N"))
$destination = Join-Path $testRoot "project"
$escapeTarget = Join-Path $testRoot "escape.txt"

function New-UnsafeArchive {
    param(
        [Parameter(Mandatory)][string]$Path,
        [Parameter(Mandatory)][string]$EntryName
    )

    $stream = [IO.File]::Open(
        $Path,
        [IO.FileMode]::CreateNew,
        [IO.FileAccess]::Write,
        [IO.FileShare]::None)
    $archive = $null
    try {
        $archive = [IO.Compression.ZipArchive]::new(
            $stream,
            [IO.Compression.ZipArchiveMode]::Create,
            $true)
        $entry = $archive.CreateEntry($EntryName)
        $writer = [IO.StreamWriter]::new(
            $entry.Open(),
            [Text.UTF8Encoding]::new($false))
        try {
            $writer.Write("must-not-extract")
        }
        finally {
            $writer.Dispose()
        }
    }
    finally {
        if ($null -ne $archive) {
            $archive.Dispose()
        }
        $stream.Dispose()
    }
}

try {
    New-Item -ItemType Directory -Path $testRoot | Out-Null
    Add-Type -AssemblyName System.IO.Compression
    $unsafeNames = @(
        "../escape.txt",
        "addons/game_agent_runtime/NUL.txt",
        "addons/game_agent_runtime/data:stream",
        "addons/game_agent_runtime/trailing."
    )
    for ($index = 0; $index -lt $unsafeNames.Count; $index++) {
        $archivePath = Join-Path $testRoot ("unsafe-$index.zip")
        New-UnsafeArchive `
            -Path $archivePath `
            -EntryName $unsafeNames[$index]
        $rejected = $false
        try {
            & $scaffoldScript `
                -Destination $destination `
                -ProjectName "Safety Test" `
                -PackageArchive $archivePath
        }
        catch {
            $rejected = $_.Exception.Message.IndexOf(
                "unsafe entry",
                [StringComparison]::OrdinalIgnoreCase) -ge 0
        }

        if (-not $rejected) {
            throw "The scaffold accepted unsafe entry '$($unsafeNames[$index])'."
        }
    }
    if (Test-Path -LiteralPath $destination) {
        throw "The scaffold published a project after rejecting its archive."
    }
    if (Test-Path -LiteralPath $escapeTarget) {
        throw "The scaffold archive escaped its staging directory."
    }

    Write-Output "SCAFFOLD_SAFETY_PASS"
}
finally {
    if (Test-Path -LiteralPath $testRoot) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $requiredPrefix = $systemTemporaryRoot `
            + [IO.Path]::DirectorySeparatorChar `
            + "gar-scaffold-safety-"
        if ($resolvedTestRoot.StartsWith(
                $requiredPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}
