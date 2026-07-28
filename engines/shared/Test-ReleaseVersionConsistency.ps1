[CmdletBinding()]
param(
    [string]$RepositoryRoot
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Read-RequiredText {
    param([Parameter(Mandatory)][string]$RelativePath)

    $path = Join-Path $script:resolvedRepositoryRoot $RelativePath
    if (-not (Test-Path -LiteralPath $path -PathType Leaf)) {
        throw "Release version input is missing: '$RelativePath'."
    }

    return [IO.File]::ReadAllText($path)
}

function Get-UniqueXmlValue {
    param(
        [Parameter(Mandatory)]
        [xml]$Document,

        [Parameter(Mandatory)]
        [string]$ElementName
    )

    $values = @(
        $Document.SelectNodes("//$ElementName") |
            ForEach-Object { $_.InnerText.Trim() } |
            Where-Object { -not [string]::IsNullOrWhiteSpace($_) } |
            Select-Object -Unique
    )
    if ($values.Count -ne 1) {
        throw "Directory.Build.props must define one unambiguous $ElementName."
    }

    return [string]$values[0]
}

function Get-OptionalUniqueXmlValue {
    param(
        [Parameter(Mandatory)]
        [xml]$Document,

        [Parameter(Mandatory)]
        [string]$ElementName
    )

    $values = @(
        $Document.SelectNodes("//$ElementName") |
            ForEach-Object { $_.InnerText.Trim() } |
            Select-Object -Unique
    )
    if ($values.Count -gt 1) {
        throw "Directory.Build.props defines an ambiguous $ElementName."
    }
    if ($values.Count -eq 0) {
        return ''
    }

    return [string]$values[0]
}

function Get-RequiredMatch {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Pattern,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $matches = @(
        [regex]::Matches(
            $Text.Replace("`r`n", "`n"),
            $Pattern,
            [Text.RegularExpressions.RegexOptions]::Multiline)
    )
    if ($matches.Count -ne 1) {
        throw "Release version metadata is missing or ambiguous for $Label."
    }

    return $matches[0].Groups['version'].Value
}

function Get-FirstMatch {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Pattern,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $match = [regex]::Match(
        $Text.Replace("`r`n", "`n"),
        $Pattern,
        [Text.RegularExpressions.RegexOptions]::Multiline)
    if (-not $match.Success) {
        throw "Release version metadata is missing for $Label."
    }

    return $match.Groups['version'].Value
}

function Assert-ReleaseVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Label,

        [Parameter(Mandatory)]
        [string]$Actual
    )

    if (-not [string]::Equals(
            $Actual,
            $script:expectedVersion,
            [StringComparison]::Ordinal)) {
        throw "$Label version '$Actual' does not match '$script:expectedVersion'."
    }
}

if ([string]::IsNullOrWhiteSpace($RepositoryRoot)) {
    $RepositoryRoot = Join-Path $PSScriptRoot '..\..'
}
$resolvedRepositoryRoot = [IO.Path]::GetFullPath($RepositoryRoot)
if (-not (Test-Path -LiteralPath $resolvedRepositoryRoot -PathType Container)) {
    throw 'RepositoryRoot must name an existing directory.'
}

try {
    [xml]$buildProps = Read-RequiredText 'Directory.Build.props'
}
catch {
    throw 'Directory.Build.props is not valid XML.'
}

$versionPrefix = Get-UniqueXmlValue `
    -Document $buildProps `
    -ElementName 'VersionPrefix'
$versionSuffix = Get-OptionalUniqueXmlValue `
    -Document $buildProps `
    -ElementName 'VersionSuffix'
$expectedVersion = if ([string]::IsNullOrWhiteSpace($versionSuffix)) {
    $versionPrefix
}
else {
    "$versionPrefix-$versionSuffix"
}
if ($expectedVersion -notmatch (
        '^[0-9]+\.[0-9]+\.[0-9]+' +
        '(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?$')) {
    throw 'Directory.Build.props does not define a supported semantic version.'
}

$rootReadmeVersion = Get-RequiredMatch `
    -Text (Read-RequiredText 'README.md') `
    -Pattern '^> Status: `(?<version>[^`]+)`\.' `
    -Label 'root README status'
Assert-ReleaseVersion 'Root README status' $rootReadmeVersion

$rootChangelogVersion = Get-FirstMatch `
    -Text (Read-RequiredText 'CHANGELOG.md') `
    -Pattern (
        '^## (?<version>[0-9]+\.[0-9]+\.[0-9]+' +
        '(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?)[ \t]*$') `
    -Label 'root changelog'
Assert-ReleaseVersion 'Root changelog' $rootChangelogVersion

$nugetReadmeVersion = Get-RequiredMatch `
    -Text (Read-RequiredText 'docs\nuget-package-readme.md') `
    -Pattern (
        '^dotnet add package GameAgent\.Runtime --version ' +
        '(?<version>[^\s]+)[ \t]*$') `
    -Label 'NuGet README install command'
Assert-ReleaseVersion 'NuGet README install command' $nugetReadmeVersion

$upmManifestText = Read-RequiredText (
    'engines\unity\com.gameagent.runtime.unity\package.json')
try {
    $upmManifest = $upmManifestText | ConvertFrom-Json
}
catch {
    throw 'The Unity package manifest is not valid JSON.'
}
$upmProperties = @($upmManifest.PSObject.Properties.Name)
if ($upmProperties -cnotcontains 'version' `
    -or [string]::IsNullOrWhiteSpace([string]$upmManifest.version)) {
    throw 'The Unity package manifest has no version.'
}
Assert-ReleaseVersion 'Unity package manifest' ([string]$upmManifest.version)

$upmChangelogVersion = Get-FirstMatch `
    -Text (Read-RequiredText (
        'engines\unity\com.gameagent.runtime.unity\CHANGELOG.md')) `
    -Pattern (
        '^## \[(?<version>[0-9]+\.[0-9]+\.[0-9]+' +
        '(?:-[0-9A-Za-z]+(?:[.-][0-9A-Za-z]+)*)?)\]' +
        '(?:[ \t]+-[ \t]+[^\r\n]+)?[ \t]*$') `
    -Label 'Unity changelog'
Assert-ReleaseVersion 'Unity changelog' $upmChangelogVersion

$unrealManifestText = Read-RequiredText (
    'engines\unreal\GameAgentRuntime.uplugin')
try {
    $unrealManifest = $unrealManifestText | ConvertFrom-Json
}
catch {
    throw 'The Unreal plugin manifest is not valid JSON.'
}
$unrealProperties = @($unrealManifest.PSObject.Properties.Name)
if ($unrealProperties -cnotcontains 'VersionName' `
    -or [string]::IsNullOrWhiteSpace(
        [string]$unrealManifest.VersionName)) {
    throw 'The Unreal plugin manifest has no VersionName.'
}
Assert-ReleaseVersion `
    'Unreal plugin manifest' `
    ([string]$unrealManifest.VersionName)

$godotPluginVersion = Get-RequiredMatch `
    -Text (Read-RequiredText (
        'engines\godot\addons\game_agent_runtime\plugin.cfg')) `
    -Pattern '^version="(?<version>[^"]+)"[ \t]*$' `
    -Label 'Godot plugin manifest'
Assert-ReleaseVersion 'Godot plugin manifest' $godotPluginVersion

$godotPackageScriptPath = Join-Path $resolvedRepositoryRoot (
    'engines\godot\tools\package-addon.ps1')
$tokens = $null
$parseErrors = $null
$godotPackageAst = [Management.Automation.Language.Parser]::ParseFile(
    $godotPackageScriptPath,
    [ref]$tokens,
    [ref]$parseErrors)
if ($parseErrors.Count -ne 0) {
    throw 'The Godot package script has PowerShell parse errors.'
}
$versionParameters = @(
    $godotPackageAst.ParamBlock.Parameters |
        Where-Object {
            $_.Name.VariablePath.UserPath -ceq 'Version'
        }
)
if ($versionParameters.Count -ne 1 `
    -or $null -eq $versionParameters[0].DefaultValue) {
    throw 'The Godot package script must define one default Version parameter.'
}
try {
    $godotPackageVersion = [string](
        $versionParameters[0].DefaultValue.SafeGetValue())
}
catch {
    throw 'The Godot package default Version must be a constant string.'
}
Assert-ReleaseVersion 'Godot package default' $godotPackageVersion

$ciVersion = Get-RequiredMatch `
    -Text (Read-RequiredText '.github\workflows\ci.yml') `
    -Pattern '^[ ]{2}RELEASE_VERSION:[ ]*["'']?(?<version>[^"''\s#]+)["'']?[ ]*(?:#.*)?$' `
    -Label 'CI release artifact'
Assert-ReleaseVersion 'CI release artifact' $ciVersion

Write-Output "RELEASE_VERSION_CONSISTENCY_PASS version=$expectedVersion"
