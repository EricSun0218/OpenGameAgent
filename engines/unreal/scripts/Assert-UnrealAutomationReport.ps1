Set-StrictMode -Version Latest

function ConvertTo-UnrealAutomationCount {
    param(
        [Parameter(Mandatory)]
        [object]$Value,

        [Parameter(Mandatory)]
        [string]$Name
    )

    if ($Value -is [string] -or $Value -is [bool]) {
        throw "The Unreal automation count '$Name' is not a JSON integer."
    }
    try {
        $decimalValue = [decimal]$Value
    }
    catch {
        throw "The Unreal automation count '$Name' is not numeric."
    }
    if ($decimalValue -lt 0 -or
        $decimalValue -gt [int64]::MaxValue -or
        $decimalValue -ne [decimal]::Truncate($decimalValue)) {
        throw "The Unreal automation count '$Name' is outside the supported range."
    }
    return [int64]$decimalValue
}

function Assert-UnrealAutomationReport {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory)]
        [string]$ReportDirectory,

        [Parameter(Mandatory)]
        [string[]]$ExpectedTests
    )

    if ($ExpectedTests.Count -eq 0) {
        throw 'At least one expected Unreal automation test is required.'
    }

    $reportFile = Join-Path $ReportDirectory 'index.json'
    if (-not (Test-Path -LiteralPath $reportFile -PathType Leaf)) {
        throw 'The Unreal automation JSON report was not generated.'
    }

    try {
        $report = Get-Content -LiteralPath $reportFile -Raw -Encoding UTF8 |
            ConvertFrom-Json
    }
    catch {
        throw 'The Unreal automation JSON report is not valid JSON.'
    }

    $requiredProperties = @(
        'succeeded',
        'succeededWithWarnings',
        'failed',
        'notRun',
        'inProcess',
        'tests'
    )
    $reportProperties = @($report.PSObject.Properties.Name)
    foreach ($propertyName in $requiredProperties) {
        if ($reportProperties -cnotcontains $propertyName) {
            throw "The Unreal automation report is missing '$propertyName'."
        }
    }

    $succeeded = ConvertTo-UnrealAutomationCount `
        -Value $report.succeeded `
        -Name 'succeeded'
    $succeededWithWarnings = ConvertTo-UnrealAutomationCount `
        -Value $report.succeededWithWarnings `
        -Name 'succeededWithWarnings'
    $failed = ConvertTo-UnrealAutomationCount `
        -Value $report.failed `
        -Name 'failed'
    $notRun = ConvertTo-UnrealAutomationCount `
        -Value $report.notRun `
        -Name 'notRun'
    $inProcess = ConvertTo-UnrealAutomationCount `
        -Value $report.inProcess `
        -Name 'inProcess'
    if ($failed -ne 0 -or $notRun -ne 0 -or $inProcess -ne 0) {
        throw "Unreal automation did not finish cleanly: failed=$failed notRun=$notRun inProcess=$inProcess."
    }

    $tests = @($report.tests)
    if ($tests.Count -eq 0) {
        throw 'The Unreal automation report did not discover any tests.'
    }
    if (($succeeded + $succeededWithWarnings) -ne $tests.Count) {
        throw 'The Unreal automation report summary does not account for every discovered test.'
    }

    foreach ($test in $tests) {
        if ($null -eq $test) {
            throw 'The Unreal automation report contains an empty test entry.'
        }
        $testProperties = @($test.PSObject.Properties.Name)
        if ($testProperties -cnotcontains 'fullTestPath' -or
            [string]::IsNullOrWhiteSpace([string]$test.fullTestPath)) {
            throw 'The Unreal automation report contains a test without a full path.'
        }
        if ($testProperties -cnotcontains 'state' -or
            [string]$test.state -cne 'Success') {
            throw "Unreal automation test '$($test.fullTestPath)' did not pass."
        }
        if ($testProperties -cnotcontains 'errors') {
            throw "Unreal automation test '$($test.fullTestPath)' has no error count."
        }
        $testErrors = ConvertTo-UnrealAutomationCount `
            -Value $test.errors `
            -Name "$($test.fullTestPath).errors"
        if ($testErrors -ne 0) {
            throw "Unreal automation test '$($test.fullTestPath)' reported $testErrors errors."
        }
    }

    foreach ($expectedTest in $ExpectedTests) {
        $matches = @(
            $tests |
                Where-Object {
                    $null -ne $_ -and
                    $_.PSObject.Properties.Name -ccontains 'fullTestPath' -and
                    [string]$_.fullTestPath -ceq $expectedTest
                }
        )
        if ($matches.Count -ne 1) {
            throw "Expected Unreal automation test '$expectedTest' was discovered $($matches.Count) times."
        }

    }

    return $report
}
