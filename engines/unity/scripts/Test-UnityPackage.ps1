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
$required = @(
    "package.json",
    "LICENSE.md",
    "THIRD PARTY NOTICES.md",
    "Runtime\GameAgent.Unity.asmdef",
    "Runtime\Plugins\GameAgent.Protocol.dll",
    "Runtime\Plugins\GameAgent.Core.dll",
    "Runtime\Plugins\GameAgent.Persistence.dll",
    "Runtime\Plugins\GameAgent.Providers.OpenAICompatible.dll",
    "Runtime\Plugins\GameAgent.Runtime.dll",
    "Runtime\Plugins\System.Text.Json.dll",
    "Tests\Runtime\UnityDurableGateScenario.cs",
    "Samples~\StructuredToolLoop\StructuredToolLoopSample.cs",
    "Documentation~\index.md",
    "SHA256SUMS"
)
foreach ($relativePath in $required) {
    $path = Join-Path $artifact $relativePath
    if (-not (Test-Path -LiteralPath $path)) {
        throw "Staged UPM artifact is missing '$relativePath'."
    }
}

$licenseText = Get-Content -LiteralPath (
    Join-Path $artifact "LICENSE.md") -Raw
if ($licenseText -notmatch "Apache License" `
    -or $licenseText -notmatch "TERMS AND CONDITIONS") {
    throw "The staged UPM artifact does not contain the complete license."
}
$noticeText = Get-Content -LiteralPath (
    Join-Path $artifact "THIRD PARTY NOTICES.md") -Raw
if ($noticeText -notmatch "Permission is hereby granted") {
    throw "The staged UPM artifact has incomplete third-party license notices."
}

$checksumPath = Join-Path $artifact "SHA256SUMS"
$checksumEntries = @{}
foreach ($line in Get-Content -LiteralPath $checksumPath) {
    if ($line -notmatch "^([0-9a-f]{64})  (Runtime/Plugins/.+\.dll)$") {
        throw "Invalid SHA256SUMS entry: '$line'."
    }

    $relativePath = $Matches[2].Replace("/", "\")
    $path = Join-Path $artifact $relativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Checksum target is missing: '$relativePath'."
    }

    $actualHash = (Get-FileHash `
        -LiteralPath $path -Algorithm SHA256).Hash
    $actual = $actualHash.ToLowerInvariant()
    if ($actual -ne $Matches[1]) {
        throw "Checksum mismatch for '$relativePath'."
    }

    $checksumEntries[$relativePath] = $true
}

$pluginDlls = Get-ChildItem `
    -LiteralPath (Join-Path $artifact "Runtime\Plugins") `
    -Filter "*.dll" -File
foreach ($pluginDll in $pluginDlls) {
    $relativePath = "Runtime\Plugins\" + $pluginDll.Name
    if (-not $checksumEntries.ContainsKey($relativePath)) {
        throw "Bundled DLL is missing from SHA256SUMS: '$relativePath'."
    }
}
if ($checksumEntries.Count -ne $pluginDlls.Count) {
    throw "SHA256SUMS does not match the bundled DLL set."
}

& (Join-Path $repositoryRoot `
    "engines\shared\Test-ReleaseArtifactPrivacy.ps1") `
    -Path $artifact

$runtimeSources = Get-ChildItem `
    -LiteralPath (Join-Path $artifact "Runtime") `
    -Filter "*.cs" -Recurse
foreach ($source in $runtimeSources) {
    $content = Get-Content -LiteralPath $source.FullName -Raw
    if ($content -match "class\s+HeadlessAgentRuntime") {
        throw "Unity SDK must not duplicate the agent core: '$($source.FullName)'."
    }
}

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
