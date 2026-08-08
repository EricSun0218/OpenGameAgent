param(
    [string] $Configuration = 'Release',
    [string] $OutputDirectory = 'artifacts/nuget',
    [string] $PackageVersion
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$outputPath = [IO.Path]::GetFullPath((Join-Path $repositoryRoot $OutputDirectory))
New-Item -ItemType Directory -Path $outputPath -Force | Out-Null

if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
    $null = Get-ReleaseVersionInfo -Version $PackageVersion
}

$packages = @(Get-ReleasePackageManifest -RepositoryRoot $repositoryRoot)
Assert-ReleasePackageManifestGraph -RepositoryRoot $repositoryRoot -Packages $packages

foreach ($package in $packages) {
    $arguments = @(
        'pack',
        $package.FullProjectPath,
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
        throw "Packing failed for '$($package.project)'."
    }
}

Write-Output $outputPath
