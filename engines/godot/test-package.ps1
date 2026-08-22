[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GodotSharpDir,
    [string] $Version = '0.3.0-alpha.4'
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$engineRoot = Split-Path -Parent (Split-Path -Parent (Split-Path -Parent $PSCommandPath))
$script = Join-Path (Split-Path -Parent $PSCommandPath) 'build-package.ps1'
$buildOutput = @(& $script -GodotSharpDir $GodotSharpDir -Version $Version)
if ($LASTEXITCODE -ne 0) { throw 'Building the Godot package failed.' }
$packageRoot = [string]$buildOutput[-1]

$required = @(
    'LICENSE',
    'addons\open_game_agent\LICENSE',
    'addons\open_game_agent\plugin.cfg',
    'addons\open_game_agent\plugin.gd',
    'addons\open_game_agent\OpenGameAgent.Godot.props',
    'addons\open_game_agent\runtime\OpenGameAgentNode.cs',
    'addons\open_game_agent\examples\minimal_local_agent\MinimalLocalAgent.cs',
    'addons\open_game_agent\lib\OpenGameAgent.Attachments.dll',
    'addons\open_game_agent\lib\OpenGameAgent.Kernel.dll',
    'addons\open_game_agent\lib\OpenGameAgent.dll',
    'addons\open_game_agent\lib\OpenGameAgent.Client.dll'
)
foreach ($relative in $required) {
    if (-not (Test-Path -LiteralPath (Join-Path $packageRoot $relative) -PathType Leaf)) {
        throw "Godot package is missing '$relative'."
    }
}

$sourceLicenseHash = (Get-FileHash -LiteralPath (Join-Path $engineRoot 'LICENSE') -Algorithm SHA256).Hash
$packagedLicenseHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'LICENSE') -Algorithm SHA256).Hash
if ($packagedLicenseHash -ne $sourceLicenseHash) {
    throw 'Godot package must contain the complete repository license.'
}
$addonLicenseHash = (Get-FileHash -LiteralPath (Join-Path $packageRoot 'addons\open_game_agent\LICENSE') -Algorithm SHA256).Hash
if ($addonLicenseHash -ne $sourceLicenseHash) {
    throw 'Godot add-on directory must contain the complete repository license.'
}

$packageRoot
