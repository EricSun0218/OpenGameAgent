[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $GodotSharpDir,
    [string] $Version = '0.3.0-alpha.2',
    [string] $OutputDirectory
)

Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'
$GodotSharpDir = [IO.Path]::GetFullPath($GodotSharpDir)
if (-not (Test-Path -LiteralPath (Join-Path $GodotSharpDir 'GodotSharp.dll') -PathType Leaf)) {
    throw 'GodotSharpDir must contain GodotSharp.dll.'
}

if ($Version -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'Version must be a semantic version.'
}

$engineRoot = Split-Path -Parent $PSCommandPath
$addonRoot = Join-Path $engineRoot 'addons\open_game_agent'
$repositoryRoot = Split-Path -Parent (Split-Path -Parent $engineRoot)
if ([string]::IsNullOrWhiteSpace($OutputDirectory)) {
    $OutputDirectory = Join-Path $repositoryRoot 'artifacts\godot'
}

$outputRoot = [IO.Path]::GetFullPath($OutputDirectory)
$packageRoot = Join-Path $outputRoot 'open-game-agent-godot'
$packagedAddon = Join-Path $packageRoot 'addons\open_game_agent'
if (-not $packageRoot.StartsWith($outputRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
    throw 'The Godot package output path is unsafe.'
}

$project = Join-Path $addonRoot 'OpenGameAgent.Godot.csproj'
& dotnet restore $project --locked-mode
if ($LASTEXITCODE -ne 0) { throw 'Godot add-on restore failed.' }
& dotnet build $project -c Release --no-restore "-p:GodotSharpDir=$GodotSharpDir" "-p:Version=$Version"
if ($LASTEXITCODE -ne 0) { throw 'Godot add-on build failed.' }

if (Test-Path -LiteralPath $packageRoot) {
    Remove-Item -LiteralPath $packageRoot -Recurse -Force
}

New-Item -ItemType Directory -Path (Join-Path $packagedAddon 'runtime'), (Join-Path $packagedAddon 'lib') -Force | Out-Null
Copy-Item -LiteralPath (Join-Path $repositoryRoot 'LICENSE') -Destination (Join-Path $packageRoot 'LICENSE')
Copy-Item -LiteralPath (Join-Path $addonRoot 'runtime\OpenGameAgentNode.cs') -Destination (Join-Path $packagedAddon 'runtime\OpenGameAgentNode.cs')
Copy-Item -LiteralPath (Join-Path $addonRoot 'OpenGameAgent.Godot.props') -Destination (Join-Path $packagedAddon 'OpenGameAgent.Godot.props')
Copy-Item -LiteralPath (Join-Path $addonRoot 'README.md') -Destination (Join-Path $packagedAddon 'README.md')
$plugin = Get-Content -LiteralPath (Join-Path $addonRoot 'plugin.cfg') -Raw
$plugin = $plugin -replace 'version="[^"]+"', ('version="' + $Version + '"')
$plugin | Set-Content -LiteralPath (Join-Path $packagedAddon 'plugin.cfg') -Encoding utf8NoBOM

$buildOutput = Join-Path $addonRoot 'bin\Release\net8.0'
foreach ($assembly in @('OpenGameAgent.Kernel.dll', 'OpenGameAgent.dll', 'OpenGameAgent.Client.dll')) {
    $source = Join-Path $buildOutput $assembly
    if (-not (Test-Path -LiteralPath $source -PathType Leaf)) {
        throw "Required Godot assembly '$assembly' is missing."
    }

    Copy-Item -LiteralPath $source -Destination (Join-Path $packagedAddon 'lib\' $assembly)
}

$packageRoot
