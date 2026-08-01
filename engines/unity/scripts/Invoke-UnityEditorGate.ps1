[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string]$UnityEditorPath,
    [Parameter(Mandatory = $true)]
    [string]$ProjectPath,
    [ValidateSet("Mono", "IL2CPP", "Both")]
    [string]$Backend = "Both"
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$editor = [IO.Path]::GetFullPath($UnityEditorPath)
$project = [IO.Path]::GetFullPath($ProjectPath)
if (-not (Test-Path -LiteralPath $editor -PathType Leaf)) {
    throw "Unity Editor executable not found: '$editor'."
}

if (-not (Test-Path -LiteralPath $project -PathType Container)) {
    throw "Unity project not found: '$project'."
}

$resultsDirectory = Join-Path $project "TestResults\GameAgentUnity"
New-Item -ItemType Directory -Path $resultsDirectory -Force |
    Out-Null
$editModeResults = Join-Path $resultsDirectory "editmode.xml"
$playModeResults = Join-Path $resultsDirectory "playmode.xml"
$editModeLog = Join-Path $resultsDirectory "editmode.log"
$playModeLog = Join-Path $resultsDirectory "playmode.log"
$monoLog = Join-Path $resultsDirectory "mono-player-gate.log"
$il2CppLog = Join-Path $resultsDirectory "il2cpp-player-gate.log"

function Invoke-UnityEditor {
    param(
        [Parameter(Mandatory = $true)]
        [string[]]$Arguments
    )

    $argumentLine = [string]::Join(
        " ",
        @(
            $Arguments | ForEach-Object {
                '"' + $_.Replace('"', '\"') + '"'
            }
        ))
    $process = Start-Process `
        -FilePath $editor `
        -ArgumentList $argumentLine `
        -PassThru `
        -WindowStyle Hidden
    try {
        $process.WaitForExit()
        return $process.ExitCode
    }
    finally {
        $process.Dispose()
    }
}

function Read-UnityLogTail {
    param([Parameter(Mandatory = $true)][string]$Path)

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        return "No Unity log was produced."
    }

    return [string]::Join(
        [Environment]::NewLine,
        @(Get-Content -LiteralPath $Path -Tail 80))
}

function Assert-UnityTestResults {
    param(
        [Parameter(Mandatory = $true)]
        [string]$Path,
        [Parameter(Mandatory = $true)]
        [string]$Label
    )

    if (-not (Test-Path -LiteralPath $Path -PathType Leaf)) {
        throw "Unity $Label tests did not produce '$Path'."
    }

    [xml]$document = Get-Content -LiteralPath $Path -Raw
    $testRun = $document.'test-run'
    if ($null -eq $testRun) {
        throw "Unity $Label test results are not valid NUnit XML."
    }

    $total = [int]$testRun.total
    $failed = [int]$testRun.failed
    if ($total -lt 1 -or $failed -ne 0 -or $testRun.result -ne "Passed") {
        throw "Unity $Label tests did not pass: " +
            "result='$($testRun.result)', total=$total, failed=$failed."
    }
}

Remove-Item -LiteralPath $editModeResults -Force -ErrorAction SilentlyContinue
$exitCode = Invoke-UnityEditor -Arguments @(
    "-batchmode",
    "-nographics",
    "-projectPath",
    $project,
    "-runTests",
    "-testPlatform",
    "EditMode",
    "-testResults",
    $editModeResults,
    "-logFile",
    $editModeLog)
if ($exitCode -ne 0) {
    throw "Unity EditMode tests failed with exit code $exitCode. " +
        (Read-UnityLogTail -Path $editModeLog)
}
Assert-UnityTestResults -Path $editModeResults -Label "EditMode"

Remove-Item -LiteralPath $playModeResults -Force -ErrorAction SilentlyContinue
$exitCode = Invoke-UnityEditor -Arguments @(
    "-batchmode",
    "-nographics",
    "-projectPath",
    $project,
    "-runTests",
    "-testPlatform",
    "PlayMode",
    "-testResults",
    $playModeResults,
    "-logFile",
    $playModeLog)
if ($exitCode -ne 0) {
    throw "Unity PlayMode tests failed with exit code $exitCode. " +
        (Read-UnityLogTail -Path $playModeLog)
}
Assert-UnityTestResults -Path $playModeResults -Label "PlayMode"

if ($Backend -eq "Mono" -or $Backend -eq "Both") {
    $exitCode = Invoke-UnityEditor -Arguments @(
        "-batchmode",
        "-nographics",
        "-quit",
        "-projectPath",
        $project,
        "-executeMethod",
        "GameAgent.Unity.Tests.UnityPlayerBuildGate.BuildWindowsMono",
        "-logFile",
        $monoLog)
    if ($exitCode -ne 0) {
        throw "Unity Mono Player build failed with exit code $exitCode. " +
            (Read-UnityLogTail -Path $monoLog)
    }
}

if ($Backend -eq "IL2CPP" -or $Backend -eq "Both") {
    $exitCode = Invoke-UnityEditor -Arguments @(
        "-batchmode",
        "-nographics",
        "-quit",
        "-projectPath",
        $project,
        "-executeMethod",
        "GameAgent.Unity.Tests.UnityPlayerBuildGate.BuildWindowsIl2Cpp",
        "-logFile",
        $il2CppLog)
    if ($exitCode -ne 0) {
        throw "Unity IL2CPP Player build failed with exit code $exitCode. " +
            (Read-UnityLogTail -Path $il2CppLog)
    }
}

Write-Host "Unity Editor gates passed for backend '$Backend'."
