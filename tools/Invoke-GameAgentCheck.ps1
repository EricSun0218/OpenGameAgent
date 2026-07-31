[CmdletBinding()]
param(
    [ValidateSet("fast", "godot", "unity", "unreal", "all")]
    [string]$Profile = "fast",

    [string]$JsonPath,

    [switch]$FailFast
)

$ErrorActionPreference = "Continue"
Set-StrictMode -Version Latest

$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$results = [Collections.Generic.List[object]]::new()
$failed = $false

function Invoke-Check {
    param(
        [Parameter(Mandatory)][string]$Id,
        [Parameter(Mandatory)][string]$Command,
        [Parameter(Mandatory)][scriptblock]$Action
    )

    $started = [DateTimeOffset]::UtcNow
    $failure = $null
    Write-Host "[check] $Id"
    try {
        $global:LASTEXITCODE = 0
        & $Action
        if ($LASTEXITCODE -ne 0) {
            throw "Command exited with code $LASTEXITCODE."
        }
    }
    catch {
        $failure = $_.Exception.Message
        $script:failed = $true
        Write-Error "$Id failed: $failure"
    }

    $duration = [DateTimeOffset]::UtcNow - $started
    $script:results.Add([ordered]@{
        id = $Id
        status = if ($null -eq $failure) { "passed" } else { "failed" }
        durationMs = [long][Math]::Ceiling($duration.TotalMilliseconds)
        command = $Command
        error = $failure
    })

    if ($script:failed -and $FailFast) {
        throw "Check profile stopped after '$Id'."
    }
}

function Invoke-FastProfile {
    Invoke-Check "managed-build" `
        "dotnet build GameAgentRuntime.sln -c Release -m:1" {
            & dotnet build GameAgentRuntime.sln -c Release -m:1
        }
    if (-not ($script:failed -and $FailFast)) {
        Invoke-Check "managed-tests" `
            "dotnet test GameAgentRuntime.sln -c Release --no-build -m:1" {
                & dotnet test GameAgentRuntime.sln `
                    -c Release --no-build -m:1
            }
    }
}

Push-Location $repositoryRoot
try {
    if ($Profile -in @("fast", "all")) {
        Invoke-FastProfile
        if (-not ($failed -and $FailFast)) {
            Invoke-Check "scaffold-safety" `
                "tools/Test-GameAgentScaffoldSafety.ps1" {
                    & .\tools\Test-GameAgentScaffoldSafety.ps1
                }
        }
    }
    if ($Profile -in @("godot", "all") `
        -and -not ($failed -and $FailFast)) {
        Invoke-Check "godot-host" `
            "engines/godot/tests/run-godot-tests.ps1" {
                & .\engines\godot\tests\run-godot-tests.ps1
            }
        if (-not ($failed -and $FailFast)) {
            Invoke-Check "godot-fresh-scaffold" `
                "tools/Test-GameAgentGodotScaffold.ps1" {
                    & .\tools\Test-GameAgentGodotScaffold.ps1
                }
        }
    }
    if ($Profile -in @("unity", "all") `
        -and -not ($failed -and $FailFast)) {
        Invoke-Check "unity-package" `
            "engines/unity/scripts/Test-UnityPackage.ps1" {
                & .\engines\unity\scripts\Test-UnityPackage.ps1
            }
    }
    if ($Profile -in @("unreal", "all") `
        -and -not ($failed -and $FailFast)) {
        Invoke-Check "unreal-portable" `
            "engines/unreal/scripts/Test-PortableWire.ps1 -RequireToolchain" {
                & .\engines\unreal\scripts\Test-PortableWire.ps1 `
                    -RequireToolchain
            }
    }
}
catch {
    $failed = $true
}
finally {
    Pop-Location
}

$report = [ordered]@{
    schemaVersion = "game-agent-check.v1"
    profile = $Profile
    success = -not $failed
    generatedAt = [DateTimeOffset]::UtcNow.ToString("O")
    checks = @($results)
}

if (-not [string]::IsNullOrWhiteSpace($JsonPath)) {
    $resolvedJsonPath = [IO.Path]::GetFullPath($JsonPath)
    $jsonParent = [IO.Path]::GetDirectoryName($resolvedJsonPath)
    if (-not [string]::IsNullOrWhiteSpace($jsonParent)) {
        New-Item -ItemType Directory -Path $jsonParent -Force | Out-Null
    }
    [IO.File]::WriteAllText(
        $resolvedJsonPath,
        ($report | ConvertTo-Json -Depth 8),
        [Text.UTF8Encoding]::new($false))
    Write-Output $resolvedJsonPath
}
else {
    $report | ConvertTo-Json -Depth 8
}

if ($failed) {
    exit 1
}
