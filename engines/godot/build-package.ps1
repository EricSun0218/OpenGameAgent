param(
    [string]$Configuration = "Release",
    [string]$Output = "$PSScriptRoot/artifacts"
)

$ErrorActionPreference = "Stop"
$root = (Resolve-Path "$PSScriptRoot/../..").Path
$outputRoot = [IO.Path]::GetFullPath($Output)
$staging = Join-Path $outputRoot "open_game_agent"

if (-not $outputRoot.StartsWith([IO.Path]::GetFullPath($PSScriptRoot), [StringComparison]::OrdinalIgnoreCase)) {
    throw "Package output must remain inside the Godot adapter directory."
}

if (Test-Path -LiteralPath $staging) {
    Remove-Item -LiteralPath $staging -Recurse -Force
}
New-Item -ItemType Directory -Path $staging | Out-Null
Copy-Item -LiteralPath "$PSScriptRoot/addons/open_game_agent" -Destination $staging -Recurse

dotnet publish "$root/engines/dotnet/OpenGameAgent.EngineClient/OpenGameAgent.EngineClient.csproj" -c $Configuration --no-restore -o "$staging/lib"
if ($LASTEXITCODE -ne 0) { exit $LASTEXITCODE }

Get-ChildItem -LiteralPath "$staging/lib" -Filter "*.pdb" | Remove-Item -Force
Compress-Archive -LiteralPath $staging -DestinationPath "$outputRoot/OpenGameAgent.Godot.zip" -Force
