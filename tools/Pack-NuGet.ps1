param(
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts/nuget',
    [string] $PackageVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

if (-not [string]::IsNullOrWhiteSpace($PackageVersion) -and
    $PackageVersion -notmatch '^[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?(?:\+[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*)?$') {
    throw 'PackageVersion must be valid SemVer.'
}

$projects = @(
    'src/OpenGameAgent.Kernel/OpenGameAgent.Kernel.csproj',
    'src/OpenGameAgent/OpenGameAgent.csproj',
    'src/OpenGameAgent.Persistence/OpenGameAgent.Persistence.csproj',
    'src/OpenGameAgent.Providers.OpenAICompatible/OpenGameAgent.Providers.OpenAICompatible.csproj',
    'src/OpenGameAgent.Providers.MediaHttp/OpenGameAgent.Providers.MediaHttp.csproj',
    'src/OpenGameAgent.Client/OpenGameAgent.Client.csproj',
    'src/OpenGameAgent.Extensions/OpenGameAgent.Extensions.csproj',
    'src/OpenGameAgent.Models/OpenGameAgent.Models.csproj',
    'src/OpenGameAgent.Connectors.Mcp/OpenGameAgent.Connectors.Mcp.csproj'
)

foreach ($project in $projects) {
    $arguments = @(
        'pack',
        (Join-Path $repositoryRoot $project),
        '-c', $Configuration,
        '--no-build',
        '--no-restore',
        '-o', $outputPath
    )

    if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
        $arguments += "-p:PackageVersion=$PackageVersion"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Packing failed for '$project'."
    }
}

Write-Output $outputPath
