[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$EngineRoot,

    [Parameter(Mandatory)]
    [string]$ProjectFile,

    [string]$ReportDirectory
)

$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Resolve-Path (Join-Path $pluginRoot '..\..')
. (Join-Path $PSScriptRoot 'Assert-UnrealAutomationReport.ps1')

$expectedTests = @(
    'GameAgent.Runtime.Unreal.WireParser',
    'GameAgent.Runtime.Unreal.GameThreadDispatcher',
    'GameAgent.Runtime.Unreal.HostRouter'
)

if ([string]::IsNullOrWhiteSpace($ReportDirectory)) {
    $ReportDirectory = Join-Path $repositoryRoot 'artifacts\unreal-automation-report'
}
$reportRoot = [System.IO.Path]::GetFullPath($ReportDirectory)
$sessionReportDirectory = Join-Path $reportRoot (
    'run-' + [guid]::NewGuid().ToString('N')
)
$null = New-Item -ItemType Directory -Path $sessionReportDirectory -Force

$resolvedEngineRoot = Resolve-Path -LiteralPath $EngineRoot
$resolvedProjectFile = Resolve-Path -LiteralPath $ProjectFile
$editorCommand = Join-Path $resolvedEngineRoot 'Engine\Binaries\Win64\UnrealEditor-Cmd.exe'
if (-not (Test-Path -LiteralPath $editorCommand -PathType Leaf)) {
    throw "UnrealEditor-Cmd.exe was not found below the supplied EngineRoot."
}

& $editorCommand `
    $resolvedProjectFile `
    -Unattended `
    -NoSplash `
    -NoSound `
    -NullRHI `
    -NoP4 `
    '-ExecCmds=Automation RunTest GameAgent.Runtime.Unreal' `
    '-TestExit=Automation Test Queue Empty' `
    "-ReportExportPath=$sessionReportDirectory" `
    -Log

if ($LASTEXITCODE -ne 0) {
    throw "Unreal automation tests failed with exit code $LASTEXITCODE."
}

$null = Assert-UnrealAutomationReport `
    -ReportDirectory $sessionReportDirectory `
    -ExpectedTests $expectedTests
Write-Host "UNREAL_AUTOMATION_PASS tests=$($expectedTests.Count) report=$sessionReportDirectory"
