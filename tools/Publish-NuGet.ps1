[CmdletBinding()]
param(
    [Parameter(Mandatory = $true)]
    [string] $Version,
    [Parameter(Mandatory = $true)]
    [string] $ApiKey,
    [string] $PackagesDirectory = 'release-assets',
    [string] $Source = 'https://api.nuget.org/v3/index.json',
    [string] $FlatContainerBaseUri = 'https://api.nuget.org/v3-flatcontainer/',
    [ValidateRange(60, 1800)]
    [int] $ValidationTimeoutSeconds = 1200,
    [ValidateRange(1, 60)]
    [int] $ValidationPollSeconds = 10,
    [ValidateRange(300, 7200)]
    [int] $OverallValidationTimeoutSeconds = 3600
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

if ([string]::IsNullOrWhiteSpace($ApiKey)) {
    throw 'A NuGet API key is required.'
}

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-ReleaseVersionInfo -Version $Version
$packages = @(Get-ReleasePackageManifest -RepositoryRoot $repositoryRoot)
Assert-ReleasePackageManifestGraph -RepositoryRoot $repositoryRoot -Packages $packages
$packageLayers = @(Get-ReleasePackageLayers -Packages $packages)

$packageRoot = if ([IO.Path]::IsPathRooted($PackagesDirectory)) {
    [IO.Path]::GetFullPath($PackagesDirectory)
}
else {
    [IO.Path]::GetFullPath((Join-Path $repositoryRoot $PackagesDirectory))
}
if (-not (Test-Path -LiteralPath $packageRoot -PathType Container)) {
    throw "NuGet package directory '$packageRoot' does not exist."
}

$expectedNames = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($package in $packages) {
    $null = $expectedNames.Add("$($package.id).$Version.nupkg")
}
$actualPackages = @(Get-ChildItem -LiteralPath $packageRoot -Filter '*.nupkg' -File)
if ($actualPackages.Count -ne $expectedNames.Count) {
    throw "Expected $($expectedNames.Count) NuGet packages but found $($actualPackages.Count)."
}
foreach ($actualPackage in $actualPackages) {
    if (-not $expectedNames.Contains($actualPackage.Name)) {
        throw "Unexpected NuGet package '$($actualPackage.Name)'."
    }
}

$baseUri = [Uri]$FlatContainerBaseUri
if (-not $baseUri.IsAbsoluteUri -or $baseUri.Scheme -ne 'https') {
    throw 'NuGet package availability checks require an absolute HTTPS base URI.'
}
$sourceUri = [Uri]$Source
if (-not $sourceUri.IsAbsoluteUri -or $sourceUri.Scheme -ne 'https') {
    throw 'NuGet publishing requires an absolute HTTPS source URI.'
}
if (-not [string]::Equals($sourceUri.AbsoluteUri, 'https://api.nuget.org/v3/index.json', [StringComparison]::OrdinalIgnoreCase) -or
    -not [string]::Equals($baseUri.AbsoluteUri, 'https://api.nuget.org/v3-flatcontainer/', [StringComparison]::OrdinalIgnoreCase)) {
    throw 'This release publisher only supports the paired NuGet.org service and availability endpoints.'
}

function Test-PublishedNuGetPackage {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [Net.Http.HttpClient] $Client,
        [Parameter(Mandatory = $true)]
        [object] $Package,
        [Parameter(Mandatory = $true)]
        [string] $LocalPackagePath,
        [Parameter(Mandatory = $true)]
        [string] $TemporaryDirectory,
        [switch] $AllowTransientUnavailable
    )

    $packageId = ([string]$Package.id).ToLowerInvariant()
    $remotePackageUri = [Uri]::new(
        $baseUri,
        "$packageId/$($versionInfo.FlatContainerVersion)/$packageId.$($versionInfo.FlatContainerVersion).nupkg")
    $remotePackagePath = Join-Path $TemporaryDirectory ($packageId + '.' + [Guid]::NewGuid().ToString('N') + '.nupkg')
    try {
        try {
            $remoteResponse = $Client.GetAsync(
                $remotePackageUri,
                [Net.Http.HttpCompletionOption]::ResponseHeadersRead).GetAwaiter().GetResult()
        }
        catch [Net.Http.HttpRequestException] {
            if ($AllowTransientUnavailable) {
                return $false
            }
            throw
        }
        catch [Threading.Tasks.TaskCanceledException] {
            if ($AllowTransientUnavailable) {
                return $false
            }
            throw
        }
        try {
            if ($remoteResponse.StatusCode -eq [Net.HttpStatusCode]::NotFound) {
                return $false
            }
            if ($AllowTransientUnavailable -and
                ($remoteResponse.StatusCode -eq [Net.HttpStatusCode]::RequestTimeout -or
                 [int]$remoteResponse.StatusCode -eq 429 -or
                 [int]$remoteResponse.StatusCode -ge 500)) {
                return $false
            }
            if (-not $remoteResponse.IsSuccessStatusCode) {
                throw "Published NuGet package download for '$($Package.id)' returned HTTP $([int]$remoteResponse.StatusCode)."
            }
            if ($null -ne $remoteResponse.Content.Headers.ContentLength -and
                $remoteResponse.Content.Headers.ContentLength -gt 314572800) {
                throw "Published NuGet package '$($Package.id)' exceeds the release size bound."
            }

            $inputStream = $remoteResponse.Content.ReadAsStreamAsync().GetAwaiter().GetResult()
            try {
                $outputStream = [IO.File]::Open(
                    $remotePackagePath,
                    [IO.FileMode]::CreateNew,
                    [IO.FileAccess]::Write,
                    [IO.FileShare]::None)
                try {
                    $buffer = [byte[]]::new(81920)
                    [long] $totalBytes = 0
                    while (($read = $inputStream.Read($buffer, 0, $buffer.Length)) -gt 0) {
                        $totalBytes += $read
                        if ($totalBytes -gt 314572800) {
                            throw "Published NuGet package '$($Package.id)' exceeds the release size bound."
                        }
                        $outputStream.Write($buffer, 0, $read)
                    }
                }
                finally {
                    $outputStream.Dispose()
                }
            }
            finally {
                $inputStream.Dispose()
            }
        }
        finally {
            $remoteResponse.Dispose()
        }

        $verificationOutput = @(& dotnet nuget verify --all $remotePackagePath --verbosity quiet 2>&1)
        if ($LASTEXITCODE -ne 0) {
            $summary = ($verificationOutput -join [Environment]::NewLine).Trim()
            if ($summary.Length -gt 2000) {
                $summary = $summary.Substring(0, 2000)
            }
            throw "Published NuGet package '$($Package.id)' failed signature verification.$([Environment]::NewLine)$summary"
        }
        $publishedHash = Get-NuGetRepositorySignedContentHash `
            -PackagePath $remotePackagePath `
            -ExpectedServiceIndexUri $sourceUri
        try {
            Assert-UnsignedNuGetPackageContentHash `
                -PackagePath $LocalPackagePath `
                -Algorithm $publishedHash.Algorithm `
                -ExpectedHash $publishedHash.HashBytes
        }
        catch {
            throw "NuGet package '$($Package.id)' version '$Version' already exists with different content."
        }

        return $true
    }
    finally {
        if (Test-Path -LiteralPath $remotePackagePath) {
            Remove-Item -LiteralPath $remotePackagePath -Force
        }
    }
}

$httpClient = [Net.Http.HttpClient]::new()
$httpClient.Timeout = [TimeSpan]::FromSeconds(120)
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('opengameagent-nuget-publish-' + [Guid]::NewGuid().ToString('N'))
$publishPlan = [Collections.Generic.List[object]]::new()
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    foreach ($package in $packages) {
        $packagePath = Join-Path $packageRoot "$($package.id).$Version.nupkg"
        if (-not (Test-Path -LiteralPath $packagePath -PathType Leaf)) {
            throw "NuGet package '$packagePath' is missing."
        }

        $alreadyPublished = Test-PublishedNuGetPackage `
            -Client $httpClient `
            -Package $package `
            -LocalPackagePath $packagePath `
            -TemporaryDirectory $temporaryRoot
        $publishPlan.Add([pscustomobject]@{
            Package = $package
            PackagePath = $packagePath
            AlreadyPublished = $alreadyPublished
        })
    }

    $planById = @{}
    foreach ($planItem in $publishPlan) {
        $planById[[string]$planItem.Package.id] = $planItem
    }
    $overallDeadline = [DateTimeOffset]::UtcNow.AddSeconds($OverallValidationTimeoutSeconds)
    foreach ($layer in $packageLayers) {
        if ([DateTimeOffset]::UtcNow -ge $overallDeadline) {
            throw "Overall NuGet validation timeout expired before dependency layer $($layer.Depth) could be published."
        }
        $pending = [Collections.Generic.List[object]]::new()
        foreach ($package in $layer.Packages) {
            $planItem = $planById[[string]$package.id]
            if ($planItem.AlreadyPublished) {
                Write-Output "NuGet package '$($planItem.Package.id)' version '$Version' is already published with verified identical content."
                continue
            }

            & dotnet nuget push $planItem.PackagePath --api-key $ApiKey --source $Source --skip-duplicate
            if ($LASTEXITCODE -ne 0) {
                throw "Publishing NuGet package '$($planItem.Package.id)' failed."
            }
            $pending.Add($planItem)
        }

        $layerDeadline = [DateTimeOffset]::UtcNow.AddSeconds($ValidationTimeoutSeconds)
        if ($overallDeadline -lt $layerDeadline) {
            $layerDeadline = $overallDeadline
        }
        while ($pending.Count -gt 0 -and [DateTimeOffset]::UtcNow -lt $layerDeadline) {
            for ($index = $pending.Count - 1; $index -ge 0; $index--) {
                $planItem = $pending[$index]
                if (Test-PublishedNuGetPackage `
                    -Client $httpClient `
                    -Package $planItem.Package `
                    -LocalPackagePath $planItem.PackagePath `
                    -TemporaryDirectory $temporaryRoot `
                    -AllowTransientUnavailable) {
                    Write-Output "NuGet package '$($planItem.Package.id)' passed remote validation."
                    $pending.RemoveAt($index)
                }
            }
            if ($pending.Count -gt 0 -and [DateTimeOffset]::UtcNow -lt $layerDeadline) {
                Start-Sleep -Seconds $ValidationPollSeconds
            }
        }
        if ($pending.Count -gt 0) {
            $pendingIds = ($pending | ForEach-Object { [string]$_.Package.id }) -join ', '
            throw "Timed out waiting for NuGet validation and indexing at dependency layer $($layer.Depth): $pendingIds."
        }
    }
}
finally {
    $httpClient.Dispose()
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
