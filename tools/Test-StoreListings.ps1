[CmdletBinding()]
param()

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$repositoryRoot = Split-Path -Parent $PSScriptRoot

foreach ($relative in @(
    'docs\store-listings\godot.md',
    'docs\store-listings\unity.md',
    'engines\godot\addons\open_game_agent\LICENSE',
    'engines\godot\addons\open_game_agent\plugin.gd',
    'engines\godot\addons\open_game_agent\examples\minimal_local_agent\MinimalLocalAgent.cs',
    'engines\unity\Packages\com.opengameagent.runtime\Third-Party Notices.txt',
    'engines\unity\Packages\com.opengameagent.runtime\Samples~\Minimal Local Agent\OpenGameAgentQuickstart.cs')) {
    if (-not (Test-Path -LiteralPath (Join-Path $repositoryRoot $relative) -PathType Leaf)) {
        throw "Store distribution is missing '$relative'."
    }
}

$manifest = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'engines\unity\Packages\com.opengameagent.runtime\package.json') -Raw |
    ConvertFrom-Json
if ($manifest.name -ne 'com.opengameagent.runtime' -or
    $manifest.unity -ne '6000.0' -or
    $manifest.unityRelease -ne '0f1' -or
    [string]::IsNullOrWhiteSpace($manifest.documentationUrl) -or
    [string]::IsNullOrWhiteSpace($manifest.licensesUrl) -or
    $manifest.samples.Count -ne 1) {
    throw 'Unity store manifest metadata is incomplete.'
}

$notices = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'engines\unity\Packages\com.opengameagent.runtime\Third-Party Notices.txt') -Raw
foreach ($assembly in @(
    'Microsoft.Bcl.AsyncInterfaces',
    'System.Buffers',
    'System.Memory',
    'System.Numerics.Vectors',
    'System.Runtime.CompilerServices.Unsafe',
    'System.Text.Encodings.Web',
    'System.Text.Json',
    'System.Threading.Tasks.Extensions')) {
    if (-not $notices.Contains($assembly, [StringComparison]::Ordinal)) {
        throw "Third-party notices do not identify '$assembly'."
    }
}

$plugin = Get-Content -LiteralPath (
    Join-Path $repositoryRoot 'engines\godot\addons\open_game_agent\plugin.cfg') -Raw
if ($plugin -notmatch '(?m)^script="plugin\.gd"$') {
    throw 'Godot add-on must declare a real editor plug-in script.'
}

$sourceLicenseHash = (Get-FileHash -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Algorithm SHA256).Hash
$addonLicenseHash = (Get-FileHash -LiteralPath (
    Join-Path $repositoryRoot 'engines\godot\addons\open_game_agent\LICENSE') -Algorithm SHA256).Hash
if ($sourceLicenseHash -ne $addonLicenseHash) {
    throw 'The Godot source add-on license must match the repository license.'
}

$artworkRoot = Join-Path $repositoryRoot 'docs\store-listings\artwork\rendered'
$artwork = @(Get-ChildItem -LiteralPath $artworkRoot -File -Filter '*.png' | Sort-Object Name)
if ($artwork.Count -ne 3) {
    throw 'Store listing must contain exactly three rendered PNG assets.'
}

foreach ($image in $artwork) {
    if ($image.Length -gt 10MB) {
        throw "Store artwork '$($image.Name)' exceeds 10 MiB."
    }

    $bytes = [IO.File]::ReadAllBytes($image.FullName)
    if ($bytes.Length -lt 24 -or
        $bytes[0] -ne 0x89 -or $bytes[1] -ne 0x50 -or $bytes[2] -ne 0x4E -or $bytes[3] -ne 0x47) {
        throw "Store artwork '$($image.Name)' is not a PNG."
    }

    $width = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 16))
    $height = [Net.IPAddress]::NetworkToHostOrder([BitConverter]::ToInt32($bytes, 20))
    if ($width -ne 1600 -or $height -ne 900) {
        throw "Store artwork '$($image.Name)' must be 1600x900."
    }
}

foreach ($listing in @('godot.md', 'unity.md')) {
    $content = Get-Content -LiteralPath (Join-Path $repositoryRoot "docs\store-listings\$listing") -Raw
    if ($content -match '(?i)\b(TBD|TODO|coming soon)\b') {
        throw "Store listing '$listing' contains unfinished copy."
    }
}

Write-Output 'Store listing metadata, notices, samples, and artwork are valid.'
