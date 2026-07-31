[CmdletBinding()]
param(
    [switch]$RequireToolchain,
    [string]$BuildDirectory,

    [ValidateSet('Auto', 'Zig')]
    [string]$Toolchain = 'Auto'
)

$ErrorActionPreference = 'Stop'
$pluginRoot = Split-Path -Parent $PSScriptRoot
$repositoryRoot = Resolve-Path (Join-Path $pluginRoot '..\..')
$sourceDirectory = Join-Path $repositoryRoot 'tests\UnrealPortableSmoke'

& (Join-Path $PSScriptRoot 'Test-UnrealAutomationReportParser.ps1')

$requestedToolchain = $Toolchain
if ($requestedToolchain -eq 'Auto' `
    -and $env:GAME_AGENT_UNREAL_PORTABLE_TOOLCHAIN -eq 'zig') {
    $requestedToolchain = 'Zig'
}

if ([string]::IsNullOrWhiteSpace($BuildDirectory)) {
    $directoryName = if ($requestedToolchain -eq 'Zig') {
        'unreal-portable-smoke-zig-toolchain'
    }
    else {
        'unreal-portable-smoke'
    }
    $BuildDirectory = Join-Path $repositoryRoot "artifacts\$directoryName"
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

$configureArguments = @(
    '-S', $sourceDirectory,
    '-B', $BuildDirectory,
    '-DCMAKE_BUILD_TYPE=Release'
)

if ($requestedToolchain -eq 'Zig') {
    $zig = Get-Command 'zig' -ErrorAction SilentlyContinue
    $ninja = Get-Command 'ninja' -ErrorAction SilentlyContinue
    if ($null -eq $zig -or $null -eq $ninja) {
        throw 'The Zig portable toolchain requires both zig and ninja on PATH.'
    }

    $zigArchiver = Join-Path $PSScriptRoot 'Invoke-ZigArchiver.cmd'
    $zigRanlib = Join-Path $PSScriptRoot 'Invoke-ZigRanlib.cmd'
    $env:GAME_AGENT_ZIG_EXECUTABLE = $zig.Source
    $configureArguments += @(
        '-G', 'Ninja',
        "-DCMAKE_MAKE_PROGRAM:FILEPATH=$($ninja.Source)",
        "-DCMAKE_C_COMPILER:FILEPATH=$($zig.Source)",
        '-DCMAKE_C_COMPILER_ARG1=cc',
        "-DCMAKE_CXX_COMPILER:FILEPATH=$($zig.Source)",
        '-DCMAKE_CXX_COMPILER_ARG1=c++',
        "-DCMAKE_AR:FILEPATH=$zigArchiver",
        "-DCMAKE_RANLIB:FILEPATH=$zigRanlib",
        "-DCMAKE_C_COMPILER_AR:FILEPATH=$zigArchiver",
        "-DCMAKE_C_COMPILER_RANLIB:FILEPATH=$zigRanlib",
        "-DCMAKE_CXX_COMPILER_AR:FILEPATH=$zigArchiver",
        "-DCMAKE_CXX_COMPILER_RANLIB:FILEPATH=$zigRanlib"
    )
}

& $cmake.Source @configureArguments
if ($LASTEXITCODE -ne 0) {
    throw 'CMake configuration failed.'
}

& $cmake.Source --build $BuildDirectory --config Release --parallel
if ($LASTEXITCODE -ne 0) {
    throw 'Portable C++ build failed.'
}

$ctestPath = Join-Path (Split-Path -Parent $cmake.Source) 'ctest.exe'
if (-not (Test-Path -LiteralPath $ctestPath)) {
    $ctest = Get-Command 'ctest' -ErrorAction SilentlyContinue
    if ($null -eq $ctest) {
        throw 'CTest was not found next to CMake or on PATH.'
    }
    $ctestPath = $ctest.Source
}

& $ctestPath --test-dir $BuildDirectory -C Release --output-on-failure
if ($LASTEXITCODE -ne 0) {
    throw 'Portable C++ smoke test failed.'
}
