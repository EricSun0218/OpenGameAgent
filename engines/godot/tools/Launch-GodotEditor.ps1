param(
    [string]$Godot = "godot",
    [string]$ProjectPath = (Split-Path -Parent $PSScriptRoot)
)

$ErrorActionPreference = "Stop"

$resolvedCommand = Get-Command $Godot -ErrorAction Stop
$commandItem = Get-Item -LiteralPath $resolvedCommand.Source
$executable = if ($commandItem.LinkType -and $commandItem.Target) {
    $targets = @($commandItem.Target)
    $target = [string]$targets[0]
    if ([System.IO.Path]::IsPathRooted($target)) {
        $target
    }
    else {
        Join-Path $commandItem.DirectoryName $target
    }
}
else {
    $resolvedCommand.Source
}

if (-not (Test-Path -LiteralPath $executable -PathType Leaf)) {
    throw "The resolved Godot executable does not exist."
}

$resolvedProjectPath = (Resolve-Path -LiteralPath $ProjectPath).Path
$quotedProjectPath = '"{0}"' -f $resolvedProjectPath

# This helper intentionally opens the interactive editor in a visible window.
Start-Process `
    -FilePath $executable `
    -ArgumentList @("--editor", "--path", $quotedProjectPath)
