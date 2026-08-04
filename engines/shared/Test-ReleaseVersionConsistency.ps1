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

function Get-WorkflowReleaseVersionDeclarations {
    param([Parameter(Mandatory)][string]$Text)

    $pattern = @'
(?m)(?:^[ \t? -]*|[{,][ \t]*)(?:"RELEASE_VERSION"|'RELEASE_VERSION'|RELEASE_VERSION)[ \t]*:[ \t]*(?:"(?<double>[^"\r\n]*)"|'(?<single>[^'\r\n]*)'|(?<plain>[^#,\s}\r\n]+))
'@.Trim()
    foreach ($match in [regex]::Matches(
            $Text.Replace("`r`n", "`n"),
            $pattern)) {
        $version = if ($match.Groups['double'].Success) {
            $match.Groups['double'].Value
        }
        elseif ($match.Groups['single'].Success) {
            $match.Groups['single'].Value
        }
        else {
            $match.Groups['plain'].Value
        }

        [pscustomobject]@{
            Index = $match.Index
            Version = $version
        }
    }
}

function Assert-LiteralWorkflowReleaseVersion {
    param(
        [Parameter(Mandatory)]
        [string]$Text,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $declarations = @(
        Get-WorkflowReleaseVersionDeclarations -Text $Text)
    if ($declarations.Count -ne 1) {
        throw "$Label must contain exactly one RELEASE_VERSION mapping."
    }

    $canonicalVersion = Get-RequiredMatch `
        -Text $Text `
        -Pattern '^[ ]{2}RELEASE_VERSION:[ ]*["'']?(?<version>[^"''\s#]+)["'']?[ ]*(?:#.*)?$' `
        -Label $Label
    if (-not [string]::Equals(
            $declarations[0].Version,
            $canonicalVersion,
            [StringComparison]::Ordinal)) {
        throw "$Label contains a non-canonical RELEASE_VERSION mapping."
    }
    Assert-ReleaseVersion $Label $canonicalVersion
}

function Assert-TrustedWorkflowReleaseVersionFlow {
    param([Parameter(Mandatory)][string]$Text)

    $normalized = $Text.Replace("`r`n", "`n")
    $expression = '${{ needs.source_privacy.outputs.release_version }}'
    $expectedJobs = @(
        'quality',
        'godot',
        'privacy',
        'validate_nuget',
        'validate_unity',
        'validate_godot')
    $declarations = @(
        Get-WorkflowReleaseVersionDeclarations -Text $normalized)
    if ($declarations.Count -ne $expectedJobs.Count -or
        @($declarations | Where-Object {
                -not [string]::Equals(
                    $_.Version,
                    $expression,
                    [StringComparison]::Ordinal)
            }).Count -ne 0) {
        throw (
            'The trusted release gate must derive every RELEASE_VERSION ' +
            'mapping from the validated candidate output.')
    }

    foreach ($job in $expectedJobs) {
        $jobMatch = [regex]::Match(
            $normalized,
            '(?ms)^  ' + [regex]::Escape($job) +
                ':[ \t]*\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:[ \t]*$|\z)')
        if (-not $jobMatch.Success) {
            throw "The trusted release gate is missing the '$job' job."
        }

        $body = $jobMatch.Groups['body'].Value
        if ($body -notmatch '(?m)^      - source_privacy[ \t]*$' -or
            $body -notmatch (
                '(?m)^      RELEASE_VERSION:[ \t]*"' +
                [regex]::Escape($expression) +
                '"[ \t]*$')) {
            throw (
                "Trusted job '$job' must depend on source_privacy and " +
                'consume its exact release_version output.')
        }
    }

    $sourcePrivacyMatch = [regex]::Match(
        $normalized,
        '(?ms)^  source_privacy:[ \t]*\n(?<body>.*?)(?=^  [A-Za-z0-9_-]+:[ \t]*$|\z)')
    if (-not $sourcePrivacyMatch.Success -or
        $sourcePrivacyMatch.Groups['body'].Value -notmatch (
            '(?m)^      release_version:[ \t]*' +
            [regex]::Escape(
                '${{ steps.release.outputs.release_version }}') +
            '[ \t]*$') -or
        $sourcePrivacyMatch.Groups['body'].Value -notmatch
            '(?m)^        id:[ \t]*release[ \t]*$') {
        throw (
            'The trusted source_privacy job must expose the validated ' +
            'release_version output from its release step.')
    }

    if ($normalized -match '(?i)\$env:RELEASE_VERSION[ \t]*=') {
        throw 'The trusted release gate must not assign RELEASE_VERSION at runtime.'
    }
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

$ciWorkflowText = Read-RequiredText '.github\workflows\ci.yml'
Assert-LiteralWorkflowReleaseVersion `
    -Text $ciWorkflowText `
    -Label 'CI release artifact'

$trustedWorkflowText = Read-RequiredText (
    '.github\workflows\trusted-source-privacy.yml')
Assert-TrustedWorkflowReleaseVersionFlow -Text $trustedWorkflowText

Write-Output "RELEASE_VERSION_CONSISTENCY_PASS version=$expectedVersion"
