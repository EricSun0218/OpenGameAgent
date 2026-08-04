[CmdletBinding()]
param(
    [string]$Solution = 'GameAgentRuntime.sln'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$solutionPath = [IO.Path]::GetFullPath($Solution)
if (-not (Test-Path -LiteralPath $solutionPath -PathType Leaf)) {
    throw "The solution does not exist: '$solutionPath'."
}
$dotnetExecutable = (
    Get-Command dotnet -CommandType Application |
        Select-Object -First 1).Source
$maximumQueryAttempts = 3
$maximumDiagnosticCharacters = 4096

function Invoke-TargetRestore {
    param([Parameter(Mandatory = $true)][string]$Target)

    & $script:dotnetExecutable restore $Target --locked-mode --verbosity quiet
    if ($LASTEXITCODE -ne 0) {
        throw "The locked dependency restore failed for '$Target'."
    }
}

function Invoke-PackageQuery {
    param(
        [Parameter(Mandatory = $true)][string]$Target,
        [Parameter(Mandatory = $true)][string[]]$Arguments
    )

    $diagnostic = ''
    for ($attempt = 1; $attempt -le $script:maximumQueryAttempts; $attempt++) {
        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = $script:dotnetExecutable
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        foreach ($argument in @(
                'list',
                $Target,
                'package') + $Arguments + @(
                '--format',
                'json',
                '--output-version',
                '1')) {
            $startInfo.ArgumentList.Add($argument)
        }

        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        try {
            if (-not $process.Start()) {
                throw 'The dotnet dependency query process did not start.'
            }
            $standardOutputTask = $process.StandardOutput.ReadToEndAsync()
            $standardErrorTask = $process.StandardError.ReadToEndAsync()
            $process.WaitForExit()
            $standardOutput = $standardOutputTask.GetAwaiter().GetResult()
            $standardError = $standardErrorTask.GetAwaiter().GetResult()
            if ($process.ExitCode -eq 0) {
                try {
                    return $standardOutput | ConvertFrom-Json
                }
                catch {
                    $diagnostic = 'The query returned invalid JSON. ' +
                        $standardError.Trim()
                }
            }
            else {
                $diagnostic = ($standardOutput + [Environment]::NewLine +
                    $standardError).Trim()
            }
        }
        finally {
            $process.Dispose()
        }

        if ($attempt -lt $script:maximumQueryAttempts) {
            Write-Warning (
                'The NuGet dependency query failed on attempt ' +
                "$attempt/$script:maximumQueryAttempts; retrying.")
            Start-Sleep -Seconds (2 * $attempt)
        }
    }

    if ($diagnostic.Length -gt $script:maximumDiagnosticCharacters) {
        $diagnostic = $diagnostic.Substring(
            $diagnostic.Length - $script:maximumDiagnosticCharacters)
    }
    throw (
        'The NuGet dependency query failed after ' +
        "$script:maximumQueryAttempts attempts: " +
        "$($Arguments -join ' ').`n$diagnostic")
}

function Get-ReportedPackages {
    param([Parameter(Mandatory = $true)]$Report)

    $reported = @()
    foreach ($project in @($Report.projects)) {
        $frameworksProperty = $project.PSObject.Properties['frameworks']
        if ($null -eq $frameworksProperty) {
            continue
        }
        foreach ($framework in @($frameworksProperty.Value)) {
            foreach ($scope in @('topLevelPackages', 'transitivePackages')) {
                $packagesProperty = $framework.PSObject.Properties[$scope]
                if ($null -eq $packagesProperty) {
                    continue
                }
                foreach ($package in @($packagesProperty.Value)) {
                    $reported += [pscustomobject]@{
                        Project = $project.path
                        Package = $package
                    }
                }
            }
        }
    }
    return $reported
}

$repositoryRoot = [IO.Path]::GetDirectoryName($solutionPath)
$targets = @(
    $solutionPath,
    (Join-Path $repositoryRoot 'engines\godot\GameAgent.Godot.csproj'),
    (Join-Path $repositoryRoot 'tests\UnityHost.Tests\UnityHost.Tests.csproj'))
if (@($targets | Where-Object {
            -not (Test-Path -LiteralPath $_ -PathType Leaf)
        }).Count -ne 0) {
    throw 'A dependency-audit target is missing.'
}

$vulnerable = @()
$deprecated = @()
$outdated = @()
foreach ($target in $targets) {
    # Package health commands require an assets file but do not reliably restore
    # standalone projects. Restore every audited target so clean CI workers and
    # developer machines produce the same result.
    Invoke-TargetRestore $target
    $vulnerable += @(Get-ReportedPackages (
            Invoke-PackageQuery $target @('--vulnerable', '--include-transitive')))
    $deprecated += @(Get-ReportedPackages (
            Invoke-PackageQuery $target @('--deprecated', '--include-transitive')))
    $outdated += @(Get-ReportedPackages (
            Invoke-PackageQuery $target @('--outdated', '--highest-minor')))
}

$findings = @()
foreach ($finding in $vulnerable) {
    $findings += "vulnerable $($finding.Package.id) $($finding.Package.resolvedVersion) in $($finding.Project)"
}
foreach ($finding in $deprecated) {
    $findings += "deprecated $($finding.Package.id) $($finding.Package.resolvedVersion) in $($finding.Project)"
}
foreach ($finding in $outdated) {
    $findings += "outdated compatible direct dependency $($finding.Package.id) $($finding.Package.resolvedVersion) -> $($finding.Package.latestVersion) in $($finding.Project)"
}
if ($findings.Count -ne 0) {
    throw "NuGet dependency health failed:`n$($findings -join [Environment]::NewLine)"
}

Write-Output 'NUGET_DEPENDENCY_HEALTH_PASS vulnerable=0 deprecated=0 outdated_compatible_direct=0'
