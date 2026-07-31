[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[0-9]+\.[0-9]+\.[0-9]+$')]
    [string]$Version,

    [Parameter(Mandatory = $true)]
    [string]$InstallPath
)

$ErrorActionPreference = 'Stop'
$resolvedInstallPath = [System.IO.Path]::GetFullPath($InstallPath)
$asset = "Godot_v$Version-stable_mono_win64.zip"
$checksum = Join-Path $PSScriptRoot "../checksums/$asset.sha512"
$checksum = [System.IO.Path]::GetFullPath($checksum)
if (-not (Test-Path -LiteralPath $checksum -PathType Leaf)) {
    throw "The pinned Godot checksum is missing for '$asset'."
}

$downloadRoot = Split-Path -Parent $resolvedInstallPath
New-Item -ItemType Directory -Force -Path $downloadRoot | Out-Null
$archive = Join-Path $downloadRoot $asset
if (-not (Test-Path -LiteralPath $archive -PathType Leaf)) {
    & curl.exe --fail --location --retry 3 --output $archive `
        "https://github.com/godotengine/godot/releases/download/$Version-stable/$asset"
    if ($LASTEXITCODE -ne 0) {
        throw 'Godot download failed.'
    }
}

$expected = ((Get-Content -LiteralPath $checksum -Raw) -split '\s+')[0]
$actual = (Get-FileHash -LiteralPath $archive -Algorithm SHA512).Hash
if (-not [string]::Equals(
        $actual,
        $expected,
        [System.StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Godot archive checksum does not match.'
}

New-Item -ItemType Directory -Force -Path $resolvedInstallPath | Out-Null
Expand-Archive -LiteralPath $archive `
    -DestinationPath $resolvedInstallPath -Force
$godot = Get-ChildItem -LiteralPath $resolvedInstallPath -Recurse `
    -Filter 'Godot_v*-stable_mono_win64.exe' -File |
    Select-Object -First 1
if ($null -eq $godot) {
    throw 'The Godot executable was not found.'
}

$godot.FullName
