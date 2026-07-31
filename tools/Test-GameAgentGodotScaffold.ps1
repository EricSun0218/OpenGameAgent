[CmdletBinding()]
param(
    [string]$Godot = "godot",
    [switch]$KeepProject
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$packageScript = Join-Path $repositoryRoot `
    "engines\godot\tools\package-addon.ps1"
$scaffoldScript = Join-Path $PSScriptRoot `
    "New-GameAgentGodotProject.ps1"
$processHelpers = Join-Path $repositoryRoot `
    "engines\godot\tests\GodotProcess.ps1"
. $processHelpers

function Resolve-RealExecutable {
    param([Parameter(Mandatory)][string]$Command)

    $resolved = Get-Command $Command -ErrorAction Stop
    $item = Get-Item -LiteralPath $resolved.Source
    if ($item.LinkType -and $item.Target) {
        $target = [string]@($item.Target)[0]
        if ([IO.Path]::IsPathRooted($target)) {
            return $target
        }

        return Join-Path $item.DirectoryName $target
    }

    return $resolved.Source
}

function Assert-NoLeakWarning {
    if ($script:LastGodotOutput -match
        '(?im)(ObjectDB.*leak(?:ed)? at exit|resources? still in use at exit|RID allocations?.*leak(?:ed)? at exit)') {
        throw "The generated project reported an engine resource leak."
    }
}

$systemTemporaryRoot = [IO.Path]::GetFullPath(
    [IO.Path]::GetTempPath()).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
$testRoot = Join-Path $systemTemporaryRoot `
    ("gar-scaffold-e2e-" + [Guid]::NewGuid().ToString("N"))
$projectRoot = Join-Path $testRoot "GeneratedGame"
try {
    $packageOutput = & $packageScript `
        -Configuration Release `
        -Version "0.1.0-scaffoldtest"
    if ($LASTEXITCODE -ne 0) {
        throw "Godot addon packaging failed."
    }

    $archive = @($packageOutput)[-1]
    $generated = & $scaffoldScript `
        -Destination $projectRoot `
        -ProjectName "Generated Agent Game" `
        -PackageArchive $archive
    if (-not [string]::Equals(
            [IO.Path]::GetFullPath(@($generated)[-1]),
            [IO.Path]::GetFullPath($projectRoot),
            [StringComparison]::OrdinalIgnoreCase)) {
        throw "The scaffold published an unexpected destination."
    }

    $executable = Resolve-RealExecutable $Godot
    $buildCode = Invoke-CheckedGodotProcess `
        -Executable $executable `
        -Arguments @(
            "--headless",
            "--path",
            $projectRoot,
            "--build-solutions",
            "--quit-after",
            "20") `
        -TimeoutSeconds 120
    if ($buildCode -ne 0) {
        throw "The generated Godot project failed to build."
    }

    $runCode = Invoke-CheckedGodotProcess `
        -Executable $executable `
        -Arguments @(
            "--headless",
            "--path",
            $projectRoot,
            "--scene",
            "res://samples/basic/BasicSample.tscn",
            "--quit-after",
            "100000") `
        -TimeoutSeconds 120
    if ($runCode -ne 0 `
        -or $script:LastGodotOutput -notmatch "GODOT_SAMPLE_PASS") {
        throw "The generated Godot project did not complete its tool loop."
    }
    Assert-NoLeakWarning
    Write-Output "GODOT_SCAFFOLD_PASS"
    if ($KeepProject) {
        Write-Output $projectRoot
    }
}
finally {
    if (-not $KeepProject -and (Test-Path -LiteralPath $testRoot)) {
        $resolvedTestRoot = [IO.Path]::GetFullPath($testRoot)
        $requiredPrefix = $systemTemporaryRoot `
            + [IO.Path]::DirectorySeparatorChar `
            + "gar-scaffold-e2e-"
        if ($resolvedTestRoot.StartsWith(
                $requiredPrefix,
                [StringComparison]::OrdinalIgnoreCase)) {
            Remove-Item -LiteralPath $resolvedTestRoot -Recurse -Force
        }
    }
}
