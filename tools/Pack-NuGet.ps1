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

$repositoryCommit = (& git -C $repositoryRoot rev-parse HEAD 2>$null).Trim()
if ($LASTEXITCODE -ne 0 -or $repositoryCommit -notmatch '^[0-9a-fA-F]{40,64}$') {
    throw 'Packing requires a repository HEAD commit.'
}

if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
    $null = Get-ReleaseVersionInfo -Version $PackageVersion
}

$packages = @(Get-ReleasePackageManifest -RepositoryRoot $repositoryRoot)
Assert-ReleasePackageManifestGraph -RepositoryRoot $repositoryRoot -Packages $packages

foreach ($package in $packages) {
    $expectedPackagePath = $null
    if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
        $expectedPackagePath = Join-Path $outputPath "$($package.id).$PackageVersion.nupkg"
        foreach ($stalePackagePath in @(
            $expectedPackagePath,
            (Join-Path $outputPath "$($package.id).$PackageVersion.snupkg")
        )) {
            if (Test-Path -LiteralPath $stalePackagePath -PathType Leaf) {
                Remove-Item -LiteralPath $stalePackagePath -Force
            }
        }
    }

    $arguments = @(
        'pack',
        $package.FullProjectPath,
        '-c', $Configuration,
        '--no-build',
        '--no-restore',
        '-o', $outputPath,
        "-p:RepositoryCommit=$repositoryCommit"
    )

    if (-not [string]::IsNullOrWhiteSpace($PackageVersion)) {
        $arguments += "-p:PackageVersion=$PackageVersion"
    }

    & dotnet @arguments
    if ($LASTEXITCODE -ne 0) {
        throw "Packing failed for '$($package.project)'."
    }
    if ($null -ne $expectedPackagePath -and
        -not (Test-Path -LiteralPath $expectedPackagePath -PathType Leaf)) {
        throw "Packing did not produce '$expectedPackagePath'."
    }
}

Write-Output $outputPath
