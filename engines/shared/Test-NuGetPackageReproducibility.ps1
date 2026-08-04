[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$FirstPath,

    [Parameter(Mandatory)]
    [string]$SecondPath
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Get-PackageMap {
    param([Parameter(Mandatory)][string]$RootPath)

    $root = [IO.Path]::GetFullPath(
        (Resolve-Path -LiteralPath $RootPath))
    $item = Get-Item -LiteralPath $root
    if (-not $item.PSIsContainer -or
        ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'A reproducibility input must be a regular directory.'
    }

    $map = [Collections.Generic.Dictionary[string, string]]::new(
        [StringComparer]::Ordinal)
    foreach ($package in Get-ChildItem `
            -LiteralPath $root `
            -File `
            -Filter '*.nupkg') {
        if (($package.Attributes -band
                [IO.FileAttributes]::ReparsePoint) -ne 0) {
            throw 'A reproducibility input contains a filesystem link.'
        }
        if ($map.ContainsKey($package.Name)) {
            throw 'A reproducibility input contains duplicate package names.'
        }
        $map.Add(
            $package.Name,
            (Get-FileHash `
                -LiteralPath $package.FullName `
                -Algorithm SHA256).Hash)
    }
    if ($map.Count -eq 0) {
        throw 'A reproducibility input contains no NuGet packages.'
    }
    return ,$map
}

$first = Get-PackageMap -RootPath $FirstPath
$second = Get-PackageMap -RootPath $SecondPath
if ($first.Count -ne $second.Count) {
    throw 'The reproducibility package sets differ.'
}
foreach ($name in $first.Keys) {
    if (-not $second.ContainsKey($name) -or
        -not $first[$name].Equals(
            $second[$name],
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'A deterministic NuGet package differs between builds.'
    }
}

Write-Output (
    'NUGET_PACKAGE_REPRODUCIBILITY_PASS packages=' + $first.Count)
