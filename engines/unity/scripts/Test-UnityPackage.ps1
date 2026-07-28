[CmdletBinding()]
param(
    [string]$UnityEditorPath,
    [string]$UnityProjectPath
)

$ErrorActionPreference = "Stop"
Set-StrictMode -Version Latest

$unityRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot ".."))
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $unityRoot "..\.."))
$smokeProject = Join-Path $repositoryRoot `
    "tests\UnityCompileSmoke\UnityCompileSmoke.csproj"
$testProject = Join-Path $repositoryRoot `
    "tests\UnityHost.Tests\UnityHost.Tests.csproj"

& dotnet build $smokeProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Unity netstandard2.1 compile smoke failed."
}

& dotnet test $testProject -c Release
if ($LASTEXITCODE -ne 0) {
    throw "Unity host conformance tests failed."
}

& (Join-Path $PSScriptRoot "Build-UpmPackage.ps1") `
    -Configuration Release -Force
if ($LASTEXITCODE -ne 0) {
    throw "UPM artifact assembly failed."
}

$artifact = Join-Path $unityRoot `
    "artifacts\com.gameagent.runtime.unity"
& (Join-Path $PSScriptRoot 'Test-UnityPackageArtifact.ps1') `
    -ArtifactPath $artifact

if ([string]::IsNullOrWhiteSpace($UnityEditorPath) `
    -xor [string]::IsNullOrWhiteSpace($UnityProjectPath)) {
    throw "Pass both -UnityEditorPath and -UnityProjectPath, or neither."
}

if (-not [string]::IsNullOrWhiteSpace($UnityEditorPath)) {
    & (Join-Path $PSScriptRoot "Invoke-UnityEditorGate.ps1") `
        -UnityEditorPath $UnityEditorPath `
        -ProjectPath $UnityProjectPath `
        -Backend "Both"
    if ($LASTEXITCODE -ne 0) {
        throw "Unity Editor gate failed."
    }
}

Write-Host "Unity package gates passed."
