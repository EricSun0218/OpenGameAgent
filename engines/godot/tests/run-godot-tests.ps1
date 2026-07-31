param(
    [string]$Godot = "godot",
    [switch]$SkipBuild
)

$ErrorActionPreference = "Stop"
$projectRoot = Split-Path -Parent $PSScriptRoot
. (Join-Path $PSScriptRoot "GodotProcess.ps1")

function Resolve-GodotExecutable {
    param([string]$Command)

    $resolved = Get-Command $Command -ErrorAction Stop
    $item = Get-Item -LiteralPath $resolved.Source
    if ($item.LinkType -and $item.Target) {
        $targets = @($item.Target)
        $target = [string]$targets[0]
        if ([System.IO.Path]::IsPathRooted($target)) {
            return $target
        }

        return Join-Path $item.DirectoryName $target
    }

    return $resolved.Source
}

function Invoke-Godot {
    param([string[]]$Arguments)

    $executable = Resolve-GodotExecutable $Godot
    return Invoke-CheckedGodotProcess `
        -Executable $executable `
        -Arguments $Arguments `
        -TimeoutSeconds 300
}

function Assert-NoGodotLeakWarning {
    if ($script:LastGodotOutput -match
        '(?im)(ObjectDB.*leak(?:ed)? at exit|resources? still in use at exit|RID allocations?.*leak(?:ed)? at exit)') {
        throw "Godot reported a leaked engine object or resource."
    }
}

if (-not $SkipBuild) {
    $buildExitCode = Invoke-Godot @(
        "--headless",
        "--path",
        $projectRoot,
        "--build-solutions",
        "--quit-after",
        "10")
    if ($buildExitCode -ne 0) {
        throw "Godot C# solution build failed with exit code $buildExitCode."
    }
}

$testExitCode = Invoke-Godot @(
    "--headless",
    "--path",
    $projectRoot,
    "--scene",
    "res://tests/HeadlessAddonTest.tscn",
    "--quit-after",
    "100000")
if ($testExitCode -ne 0) {
    throw "Godot headless addon tests failed with exit code $testExitCode."
}
if ($script:LastGodotOutput -notmatch "GODOT_TEST_PASS") {
    throw "Godot headless addon tests exited without the GODOT_TEST_PASS marker."
}
Assert-NoGodotLeakWarning

$sampleExitCode = Invoke-Godot @(
    "--headless",
    "--path",
    $projectRoot,
    "--scene",
    "res://samples/basic/BasicSample.tscn",
    "--quit-after",
    "100000")
if ($sampleExitCode -ne 0) {
    throw "Godot basic sample smoke failed with exit code $sampleExitCode."
}
if ($script:LastGodotOutput -notmatch "GODOT_SAMPLE_PASS") {
    throw "Godot basic sample exited without the GODOT_SAMPLE_PASS marker."
}
Assert-NoGodotLeakWarning
