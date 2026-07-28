[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
. (Join-Path $PSScriptRoot 'Assert-UnrealAutomationReport.ps1')

$expectedTests = @(
    'GameAgent.Runtime.Unreal.WireParser',
    'GameAgent.Runtime.Unreal.GameThreadDispatcher',
    'GameAgent.Runtime.Unreal.HostRouter'
)
$temporaryRoot = Join-Path (
    [System.IO.Path]::GetTempPath()
) ('game-agent-unreal-report-' + [guid]::NewGuid().ToString('N'))

function Write-SyntheticReport {
    param(
        [Parameter(Mandatory)]
        [string]$Directory,

        [Parameter(Mandatory)]
        [object[]]$Tests,

        [int64]$Succeeded,
        [int64]$Failed,
        [int64]$NotRun = 0,
        [int64]$InProcess = 0
    )

    $null = New-Item -ItemType Directory -Path $Directory -Force
    $report = [ordered]@{
        succeeded = $Succeeded
        succeededWithWarnings = 0
        failed = $Failed
        notRun = $NotRun
        inProcess = $InProcess
        tests = $Tests
    }
    $json = $report | ConvertTo-Json -Depth 8
    [System.IO.File]::WriteAllText(
        (Join-Path $Directory 'index.json'),
        $json,
        [System.Text.UTF8Encoding]::new($false)
    )
}

function Assert-SyntheticReportRejected {
    param(
        [Parameter(Mandatory)]
        [scriptblock]$Assertion,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    $rejected = $false
    try {
        $null = & $Assertion
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw $FailureMessage
    }
}

try {
    $passingTests = @(
        foreach ($testName in $expectedTests) {
            [pscustomobject]@{
                fullTestPath = $testName
                state = 'Success'
                errors = 0
            }
        }
    )
    $passingDirectory = Join-Path $temporaryRoot 'passing'
    Write-SyntheticReport `
        -Directory $passingDirectory `
        -Tests $passingTests `
        -Succeeded $passingTests.Count `
        -Failed 0
    $null = Assert-UnrealAutomationReport `
        -ReportDirectory $passingDirectory `
        -ExpectedTests $expectedTests

    $missingDirectory = Join-Path $temporaryRoot 'missing-test'
    Write-SyntheticReport `
        -Directory $missingDirectory `
        -Tests @($passingTests[0], $passingTests[1]) `
        -Succeeded 2 `
        -Failed 0
    Assert-SyntheticReportRejected `
        -FailureMessage 'The report parser accepted a missing expected test.' `
        -Assertion {
            Assert-UnrealAutomationReport `
                -ReportDirectory $missingDirectory `
                -ExpectedTests $expectedTests
        }

    $duplicateDirectory = Join-Path $temporaryRoot 'duplicate-test'
    $duplicateTests = @($passingTests) + @($passingTests[2])
    Write-SyntheticReport `
        -Directory $duplicateDirectory `
        -Tests $duplicateTests `
        -Succeeded $duplicateTests.Count `
        -Failed 0
    Assert-SyntheticReportRejected `
        -FailureMessage 'The report parser accepted a duplicate expected test.' `
        -Assertion {
            Assert-UnrealAutomationReport `
                -ReportDirectory $duplicateDirectory `
                -ExpectedTests $expectedTests
        }

    $failedTests = @($passingTests)
    $failedTests[2] = [pscustomobject]@{
        fullTestPath = $expectedTests[2]
        state = 'Fail'
        errors = 1
    }
    $failedDirectory = Join-Path $temporaryRoot 'failed-test'
    Write-SyntheticReport `
        -Directory $failedDirectory `
        -Tests $failedTests `
        -Succeeded 2 `
        -Failed 1
    Assert-SyntheticReportRejected `
        -FailureMessage 'The report parser accepted a failed expected test.' `
        -Assertion {
            Assert-UnrealAutomationReport `
                -ReportDirectory $failedDirectory `
                -ExpectedTests $expectedTests
        }

    $emptyDirectory = Join-Path $temporaryRoot 'missing-report'
    $null = New-Item -ItemType Directory -Path $emptyDirectory -Force
    Assert-SyntheticReportRejected `
        -FailureMessage 'The report parser accepted a missing report.' `
        -Assertion {
            Assert-UnrealAutomationReport `
                -ReportDirectory $emptyDirectory `
                -ExpectedTests $expectedTests
        }

    Write-Host 'UNREAL_AUTOMATION_REPORT_PARSER_PASS'
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
