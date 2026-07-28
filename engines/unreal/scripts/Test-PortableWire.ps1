[CmdletBinding()]
param(
    [switch]$RequireToolchain,
    [string]$BuildDirectory
)

$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Resolve-Path (Join-Path $pluginRoot '..\..')
$sourceDirectory = Join-Path $repositoryRoot 'tests\UnrealPortableSmoke'

& (Join-Path $PSScriptRoot 'Test-UnrealAutomationReportParser.ps1')

if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $BuildDirectory = Join-Path $repositoryRoot 'artifacts\unreal-portable-smoke'
}

$cmake = Get-Command 'cmake' -ErrorAction SilentlyContinue
if ($null -eq $cmake) {
    $message = 'Unreal portable smoke gate skipped: CMake and a C++17 toolchain are required.'
    if ($RequireToolchain) {
        throw $message
    }
    Write-Warning $message
    exit 0
}

& $cmake.Source -S $sourceDirectory -B $BuildDirectory -DCMAKE_BUILD_TYPE=Release
if ($LASTEXITCODE -ne 0) {
    throw 'CMake configuration failed.'
}

& $cmake.Source --build $BuildDirectory --config Release --parallel
if ($LASTEXITCODE -ne 0) {
    throw 'Portable C++ build failed.'
}

& ctest --test-dir $BuildDirectory -C Release --output-on-failure
if ($LASTEXITCODE -ne 0) {
    throw 'Portable C++ smoke test failed.'
}
