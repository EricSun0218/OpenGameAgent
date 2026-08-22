[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [ValidatePattern('^[a-z0-9]+(?:[.-][a-z0-9]+)*$')]
    [string] $Id,

    [Parameter(Mandatory = $true)]
    [string] $OutputDirectory,

    [string] $Namespace,

    [string] $OpenGameAgentExtensionsProject
)

$ErrorActionPreference = 'Stop'
$repoRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$templateRoot = Join-Path $repoRoot 'templates/game-agent-extension'
$destination = [IO.Path]::GetFullPath($OutputDirectory)

if (-not $Namespace) {
    $segments = $Id -split '[.-]' | ForEach-Object {
        if ($_.Length -eq 1) { $_.ToUpperInvariant() } else { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }
    }
    $Namespace = 'OpenGameAgent.Extension.' + ($segments -join '')
}

if ($Namespace -notmatch '^[A-Za-z_][A-Za-z0-9_.]*$' -or $Namespace.Length -gt 256) {
    throw 'Namespace must be a bounded valid C# namespace.'
}

if (Test-Path -LiteralPath $destination) {
    if (-not (Test-Path -LiteralPath $destination -PathType Container)) {
        throw "Extension destination '$destination' is not a directory."
    }

    if (Get-ChildItem -LiteralPath $destination -Force | Select-Object -First 1) {
        throw "Extension destination '$destination' must be empty."
    }
} else {
    $null = New-Item -ItemType Directory -Path $destination
}

if (-not $OpenGameAgentExtensionsProject) {
    $OpenGameAgentExtensionsProject = Join-Path $repoRoot 'src/OpenGameAgent.Extensions/OpenGameAgent.Extensions.csproj'
}
$extensionsProject = [IO.Path]::GetFullPath($OpenGameAgentExtensionsProject)
if (-not (Test-Path -LiteralPath $extensionsProject -PathType Leaf)) {
    throw "OpenGameAgent.Extensions project '$extensionsProject' does not exist."
}

$typeName = (($Id -split '[.-]' | ForEach-Object {
    if ($_.Length -eq 1) { $_.ToUpperInvariant() } else { $_.Substring(0, 1).ToUpperInvariant() + $_.Substring(1) }
}) -join '') + 'Extension'
$relativeProject = [IO.Path]::GetRelativePath($destination, $extensionsProject).Replace('\', '/')
$utf8 = [Text.UTF8Encoding]::new($false)

$replacements = @{
    '__EXTENSION_ID__' = $Id
    '__NAMESPACE__' = $Namespace
    '__TYPE_NAME__' = $typeName
    '__OGA_EXTENSIONS_PROJECT__' = $relativeProject
}

foreach ($template in Get-ChildItem -LiteralPath $templateRoot -Filter '*.template' -File) {
    $content = [IO.File]::ReadAllText($template.FullName)
    foreach ($pair in $replacements.GetEnumerator()) {
        $content = $content.Replace([string] $pair.Key, [string] $pair.Value)
    }

    $name = $template.Name.Substring(0, $template.Name.Length - '.template'.Length)
    [IO.File]::WriteAllText((Join-Path $destination $name), $content, $utf8)
}

Write-Host "Created extension '$Id' in '$destination'."
Write-Host "Build: dotnet build '$([IO.Path]::Combine($destination, 'Extension.csproj'))' -c Release"
