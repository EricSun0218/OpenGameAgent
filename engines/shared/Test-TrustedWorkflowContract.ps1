[CmdletBinding()]
param(
    [string]$WorkflowPath = (Join-Path $PSScriptRoot (
        '..\..\.github\workflows\trusted-source-privacy.yml')),

    [switch]$SelfTest
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-IndentedBlock {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyString()]
        [string[]]$Lines,

        [Parameter(Mandatory)]
        [string]$StartPattern,

        [Parameter(Mandatory)]
        [string]$StopPattern,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $start = -1
    for ($index = 0; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -cmatch $StartPattern) {
            $start = $index
            break
        }
    }
    if ($start -lt 0) {
        throw "The trusted workflow is missing the $Label block."
    }

    $end = $Lines.Count
    for ($index = $start + 1; $index -lt $Lines.Count; $index++) {
        if ($Lines[$index] -cmatch $StopPattern) {
            $end = $index
            break
        }
    }

    return ($Lines[$start..($end - 1)] -join "`n")
}

function Assert-TrustedWorkflowContract {
    param(
        [Parameter(Mandatory)]
        [string]$Text
    )

    $lines = @($Text -split "\r?\n")
    $privacy = Get-IndentedBlock `
        -Lines $lines `
        -StartPattern '^  privacy:$' `
        -StopPattern '^  [A-Za-z0-9_]+:$' `
        -Label 'privacy job'

    $countSource = '${{ needs.source_privacy.outputs.expected_nuget_package_count }}'
    if (-not $privacy.Contains(
            "      EXPECTED_NUGET_PACKAGE_COUNT: `"$countSource`"")) {
        throw 'The privacy job must receive the trusted expected package count.'
    }
    if (-not $privacy.Contains(
            '$expectedNuGetPackageCount = [int]::Parse(')) {
        throw 'The privacy job must validate and parse the expected package count.'
    }

    $dynamicCounts = [regex]::Matches(
        $privacy,
        'Expected = \$expectedNuGetPackageCount').Count
    if ($dynamicCounts -ne 2) {
        throw 'Both recovered NuGet sets must use the trusted expected package count.'
    }
    if ($privacy -cmatch (
            "(?s)Source = '\./sealed-artifacts/nuget/(?:raw-a|raw-b)'" +
            '.{0,240}?Expected = [0-9]+')) {
        throw 'A recovered NuGet set must not use a hard-coded package count.'
    }

    $status = Get-IndentedBlock `
        -Lines $lines `
        -StartPattern '^      - name: Publish immutable candidate status$' `
        -StopPattern '^      - name:' `
        -Label 'immutable candidate status step'
    if ($status -cnotmatch '(?m)^          TRUSTED_RUN_URL: \$\{\{ .+ \}\}$') {
        throw 'The status step must declare TRUSTED_RUN_URL in its own environment.'
    }
    if (-not $status.Contains('-f "target_url=$env:TRUSTED_RUN_URL"')) {
        throw 'The status step must publish the declared trusted run URL.'
    }
}

$resolvedWorkflowPath = [IO.Path]::GetFullPath($WorkflowPath)
if (-not [IO.File]::Exists($resolvedWorkflowPath)) {
    throw "Trusted workflow not found: $resolvedWorkflowPath"
}
$workflowText = [IO.File]::ReadAllText($resolvedWorkflowPath)
Assert-TrustedWorkflowContract -Text $workflowText

if ($SelfTest) {
    $fixtures = @(
        @{
            Name = 'status URL scope'
            Text = $workflowText -replace (
                '(?m)^          TRUSTED_RUN_URL:.*\r?\n'), ''
        },
        @{
            Name = 'hard-coded package counts'
            Text = $workflowText.Replace(
                'Expected = $expectedNuGetPackageCount',
                'Expected = 10')
        },
        @{
            Name = 'missing package-count source'
            Text = $workflowText -replace (
                '(?m)^      EXPECTED_NUGET_PACKAGE_COUNT:.*' +
                'needs\.source_privacy.*\r?\n'), ''
        }
    )

    foreach ($fixture in $fixtures) {
        $rejected = $false
        try {
            Assert-TrustedWorkflowContract -Text $fixture.Text
        }
        catch {
            $rejected = $true
        }
        if (-not $rejected) {
            throw "The workflow contract accepted the $($fixture.Name) regression."
        }
    }

    Write-Output 'TRUSTED_WORKFLOW_CONTRACT_SELF_TEST_PASS fixtures=3'
}

Write-Output 'TRUSTED_WORKFLOW_CONTRACT_PASS'
