[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$checker = Join-Path $PSScriptRoot 'Test-ReleaseVersionConsistency.ps1'
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) ('game-agent-version-consistency-' + [guid]::NewGuid().ToString('N'))
$expectedVersion = '0.1.0-alpha.1'
$driftVersion = '9.9.9-test'

function Write-FixtureFile {
    param(
        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [string]$Content
    )

    $path = Join-Path $script:temporaryRoot $RelativePath
    $directory = Split-Path -Parent $path
    $null = New-Item -ItemType Directory -Path $directory -Force
    [IO.File]::WriteAllText(
        $path,
        $Content.Replace("`r`n", "`n"),
        [Text.UTF8Encoding]::new($false))
}

function Reset-Fixture {
    param(
        [string]$VersionPrefix = '0.1.0',
        [AllowEmptyString()]
        [string]$VersionSuffix = 'alpha.1',
        [switch]$OmitVersionSuffix
    )

    $fixtureVersion = if ([string]::IsNullOrWhiteSpace($VersionSuffix)) {
        $VersionPrefix
    }
    else {
        "$VersionPrefix-$VersionSuffix"
    }
    $versionSuffixElement = if ($OmitVersionSuffix) {
        ''
    }
    else {
        "    <VersionSuffix>$VersionSuffix</VersionSuffix>`n"
    }

    $files = [ordered]@{
        'Directory.Build.props' = @"
<Project>
  <PropertyGroup>
    <VersionPrefix>$VersionPrefix</VersionPrefix>
$versionSuffixElement  </PropertyGroup>
</Project>
"@
        'README.md' = @"
# Fixture

> Status: ``$fixtureVersion``. Test fixture.
"@
        'CHANGELOG.md' = @"
# Changelog

## Unreleased

## $fixtureVersion

## 0.0.1
"@
        'engines\unity\com.gameagent.runtime.unity\package.json' = @"
{"name":"com.gameagent.runtime.unity","version":"$fixtureVersion"}
"@
        'engines\unity\com.gameagent.runtime.unity\CHANGELOG.md' = @"
# Changelog

## [Unreleased]

## [$fixtureVersion] - 2026-07-29

## [0.0.1] - 2026-01-01
"@
        'engines\unreal\GameAgentRuntime.uplugin' = @"
{"FileVersion":3,"Version":1,"VersionName":"$fixtureVersion"}
"@
        'engines\godot\addons\game_agent_runtime\plugin.cfg' = @"
[plugin]
version="$fixtureVersion"
"@
        'engines\godot\tools\package-addon.ps1' = @"
param(
    [string]`$Version = "$fixtureVersion"
)
"@
        '.github\workflows\ci.yml' = @"
name: CI
env:
  RELEASE_VERSION: "$fixtureVersion"
"@
    }

    foreach ($entry in $files.GetEnumerator()) {
        Write-FixtureFile `
            -RelativePath $entry.Key `
            -Content $entry.Value
    }
}

function Assert-FixtureRejected {
    param(
        [Parameter(Mandatory)]
        [string]$Name,

        [Parameter(Mandatory)]
        [string]$RelativePath,

        [Parameter(Mandatory)]
        [string]$Content
    )

    Reset-Fixture
    Write-FixtureFile -RelativePath $RelativePath -Content $Content
    $rejected = $false
    try {
        $null = & $script:checker -RepositoryRoot $script:temporaryRoot
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "The release version checker accepted '$Name' drift."
    }
}

$null = New-Item -ItemType Directory -Path $temporaryRoot -Force
try {
    Reset-Fixture
    $passOutput = @(
        & $checker -RepositoryRoot $temporaryRoot
    )
    if ($passOutput -notcontains (
            "RELEASE_VERSION_CONSISTENCY_PASS version=$expectedVersion")) {
        throw 'The release version checker did not accept a consistent fixture.'
    }

    Reset-Fixture -VersionPrefix '1.0.0' -VersionSuffix ''
    $stableOutput = @(
        & $checker -RepositoryRoot $temporaryRoot
    )
    if ($stableOutput -notcontains (
            'RELEASE_VERSION_CONSISTENCY_PASS version=1.0.0')) {
        throw 'The release version checker rejected an empty stable suffix.'
    }

    Reset-Fixture `
        -VersionPrefix '1.0.0' `
        -VersionSuffix '' `
        -OmitVersionSuffix
    $stableWithoutSuffixOutput = @(
        & $checker -RepositoryRoot $temporaryRoot
    )
    if ($stableWithoutSuffixOutput -notcontains (
            'RELEASE_VERSION_CONSISTENCY_PASS version=1.0.0')) {
        throw 'The release version checker rejected a missing stable suffix.'
    }

    Assert-FixtureRejected `
        -Name 'root README' `
        -RelativePath 'README.md' `
        -Content "> Status: ``$driftVersion``. Test fixture."
    Assert-FixtureRejected `
        -Name 'ambiguous version suffix' `
        -RelativePath 'Directory.Build.props' `
        -Content @"
<Project>
  <PropertyGroup>
    <VersionPrefix>0.1.0</VersionPrefix>
    <VersionSuffix>alpha.1</VersionSuffix>
    <VersionSuffix>beta.1</VersionSuffix>
  </PropertyGroup>
</Project>
"@
    Assert-FixtureRejected `
        -Name 'root changelog' `
        -RelativePath 'CHANGELOG.md' `
        -Content "## $driftVersion`n`n## $expectedVersion"
    Assert-FixtureRejected `
        -Name 'Unity package manifest' `
        -RelativePath (
            'engines\unity\com.gameagent.runtime.unity\package.json') `
        -Content (
            '{"name":"com.gameagent.runtime.unity","version":"' +
            $driftVersion +
            '"}')
    Assert-FixtureRejected `
        -Name 'Unity changelog' `
        -RelativePath (
            'engines\unity\com.gameagent.runtime.unity\CHANGELOG.md') `
        -Content (
            "## [$driftVersion] - 2026-07-29`n`n" +
            "## [$expectedVersion] - 2026-01-01")
    Assert-FixtureRejected `
        -Name 'Unreal plugin manifest' `
        -RelativePath 'engines\unreal\GameAgentRuntime.uplugin' `
        -Content (
            '{"FileVersion":3,"Version":1,"VersionName":"' +
            $driftVersion +
            '"}')
    Assert-FixtureRejected `
        -Name 'Godot plugin manifest' `
        -RelativePath (
            'engines\godot\addons\game_agent_runtime\plugin.cfg') `
        -Content "version=`"$driftVersion`""
    Assert-FixtureRejected `
        -Name 'Godot package default' `
        -RelativePath 'engines\godot\tools\package-addon.ps1' `
        -Content "param([string]`$Version = `"$driftVersion`")"
    Assert-FixtureRejected `
        -Name 'Godot package script syntax' `
        -RelativePath 'engines\godot\tools\package-addon.ps1' `
        -Content 'param([string]$Version = "unterminated)'
    Assert-FixtureRejected `
        -Name 'CI release artifact' `
        -RelativePath '.github\workflows\ci.yml' `
        -Content "env:`n  RELEASE_VERSION: `"$driftVersion`""

    Write-Output 'RELEASE_VERSION_CONSISTENCY_SELF_TEST_PASS'
}
finally {
    $resolvedTemporaryRoot = [IO.Path]::GetFullPath($temporaryRoot)
    $systemTemporaryRoot = [IO.Path]::GetFullPath(
        [IO.Path]::GetTempPath()).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
    $requiredPrefix = $systemTemporaryRoot +
        [IO.Path]::DirectorySeparatorChar
    $comparison = if (
        [Environment]::OSVersion.Platform -eq [PlatformID]::Win32NT
    ) {
        [StringComparison]::OrdinalIgnoreCase
    }
    else {
        [StringComparison]::Ordinal
    }
    $isExpectedTemporaryDirectory = (
        $resolvedTemporaryRoot.StartsWith($requiredPrefix, $comparison) -and
        [IO.Path]::GetFileName($resolvedTemporaryRoot).StartsWith(
            'game-agent-version-consistency-',
            [StringComparison]::Ordinal)
    )
    if ($isExpectedTemporaryDirectory `
        -and (Test-Path -LiteralPath $resolvedTemporaryRoot)) {
        Remove-Item -LiteralPath $resolvedTemporaryRoot -Recurse -Force
    }
}
