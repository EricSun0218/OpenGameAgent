[CmdletBinding()]
param(
    [string] $Version = '0.3.0-alpha.4'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

. (Join-Path $PSScriptRoot 'Release.Common.ps1')

$repositoryRoot = Split-Path -Parent $PSScriptRoot
$versionInfo = Get-ReleaseVersionInfo -Version $Version
if ($versionInfo.Version -ne $Version) {
    throw 'Release version validation changed the supplied version.'
}

$prereleaseProbe = Get-ReleaseVersionInfo -Version '1.2.3-rc.1'
if (-not $prereleaseProbe.IsPrerelease) {
    throw 'Prerelease version classification failed.'
}
$stableProbe = Get-ReleaseVersionInfo -Version '1.2.3'
if ($stableProbe.IsPrerelease) {
    throw 'Stable version classification failed.'
}
if ($stableProbe.Major -ne 1 -or $stableProbe.Minor -ne 2 -or $stableProbe.Patch -ne 3) {
    throw 'Release version numeric component parsing failed.'
}
$maximumNumericProbe = Get-ReleaseVersionInfo -Version '2147483647.2147483647.2147483647'
if ($maximumNumericProbe.Major -ne [int]::MaxValue) {
    throw 'Release version Int32 boundary parsing failed.'
}
if ((Get-ReleaseStabilityNotice -VersionInfo (Get-ReleaseVersionInfo -Version '0.9.0')) -notmatch 'Before 1\.0') {
    throw 'Pre-1.0 stable release notice classification failed.'
}
if ((Get-ReleaseStabilityNotice -VersionInfo $stableProbe) -match 'Before 1\.0') {
    throw 'Post-1.0 stable release notice classification failed.'
}
if ((Get-ReleaseStabilityNotice -VersionInfo $prereleaseProbe) -notmatch 'pre-release') {
    throw 'Prerelease notice classification failed.'
}

foreach ($invalidVersion in @(
    '1.2.3+build.7',
    '01.2.3',
    '1.02.3',
    '1.2.03',
    '1.2.3-rc.01',
    '../1.2.3',
    '2147483648.0.0',
    '0.2147483648.0',
    '0.0.2147483648'
)) {
    $rejected = $false
    try {
        $null = Get-ReleaseVersionInfo -Version $invalidVersion
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw "Invalid release version '$invalidVersion' was accepted."
    }
}

$packages = @(Get-ReleasePackageManifest -RepositoryRoot $repositoryRoot)
Assert-ReleasePackageManifestGraph -RepositoryRoot $repositoryRoot -Packages $packages
if (-not (Test-SupportedPortableServerRuntimeAsset -AssetPath 'runtimes/linux-x64/native/libExample.so') -or
    -not (Test-SupportedPortableServerRuntimeAsset -AssetPath 'runtimes/win-x64/native/Example.dll') -or
    (Test-SupportedPortableServerRuntimeAsset -AssetPath 'runtimes/win-x64/native/Example.pdb') -or
    (Test-SupportedPortableServerRuntimeAsset -AssetPath 'runtimes/osx/native/libExample.dylib')) {
    throw 'Portable server asset filtering must retain Windows/Linux runtime files and exclude symbols/macOS assets.'
}
$godotDownloadPattern = "Godot_v4\.7\.1-stable_mono_win64\.zip'.*-MaximumRetryCount\s+4\s+-RetryIntervalSec\s+5"
foreach ($workflowPath in @('.github\workflows\ci.yml', '.github\workflows\release.yml')) {
    $workflow = Get-Content -LiteralPath (Join-Path $repositoryRoot $workflowPath) -Raw
    if ($workflow -notmatch $godotDownloadPattern) {
        throw "Godot download in '$workflowPath' must use bounded transient retries before checksum verification."
    }
}
$packScript = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools\Pack-NuGet.ps1') -Raw
$removesExactVersionedPackage =
    $packScript -match '\$\(\$package\.id\)\.\$PackageVersion\.nupkg' -and
    $packScript -match '\$\(\$package\.id\)\.\$PackageVersion\.snupkg' -and
    $packScript -match 'Remove-Item\s+-LiteralPath\s+\$stalePackagePath'
$pinsRepositoryCommit = $packScript -match '-p:RepositoryCommit=\$repositoryCommit'
$assertsExpectedPackage = $packScript -match "Packing did not produce"
if (-not ($removesExactVersionedPackage -and $pinsRepositoryCommit -and $assertsExpectedPackage)) {
    throw 'NuGet packing must replace exact same-version outputs, pin HEAD, and verify each result.'
}
$godotSmokeScript = Get-Content -LiteralPath (Join-Path $repositoryRoot 'engines\godot\test-engine.ps1') -Raw
$startsGodotProcess = $godotSmokeScript -match 'Start-Process'
$waitsForGodotProcess = $godotSmokeScript -match '(?m)^\s*-Wait\s*`?\s*$'
$requiresGodotMarker = $godotSmokeScript -match 'OPENGAMEAGENT_GODOT_SMOKE_OK'
if (-not ($startsGodotProcess -and $waitsForGodotProcess -and $requiresGodotMarker)) {
    throw 'The Godot real-editor gate must wait for the editor process and require the runtime smoke marker.'
}
$unrealPackageScript = Get-Content -LiteralPath (Join-Path $repositoryRoot 'engines\unreal\test-package.ps1') -Raw
$unrealSmokeScript = Get-Content -LiteralPath (Join-Path $repositoryRoot 'engines\unreal\test-plugin.ps1') -Raw
$releaseWorkflow = Get-Content -LiteralPath (Join-Path $repositoryRoot '.github\workflows\release.yml') -Raw
$releaseBundle = Get-Content -LiteralPath (Join-Path $repositoryRoot 'tools\New-ReleaseBundle.ps1') -Raw
if ($releaseBundle -notmatch 'RELEASE_MANIFEST\.json' -or
    $releaseBundle -notmatch 'sourceCommit\s*=\s*\$SourceCommit' -or
    $releaseBundle -notmatch 'runtimeProtocolVersions\s*=\s*@\(1\)' -or
    $releaseBundle -notmatch 'SHA256SUMS\.txt') {
    throw 'Release bundles must record source provenance, Runtime Protocol compatibility, and SHA-256 verification.'
}
if ($unrealPackageScript -notmatch 'OpenGameAgent\.uplugin' -or
    $unrealSmokeScript -notmatch 'OpenGameAgent\.Unreal\.Tests' -or
    $unrealSmokeScript -notmatch 'OPENGAMEAGENT_UNREAL_SMOKE_OK' -or
    $releaseWorkflow -notmatch 'engines/unreal/test-package\.ps1' -or
    $releaseBundle -notmatch 'OpenGameAgent\.Unreal-\$Version\.zip') {
    throw 'The Unreal source plugin must be validated and included in release assets.'
}
$packageLayers = @(Get-ReleasePackageLayers -Packages $packages)
$layeredPackages = @($packageLayers | ForEach-Object { $_.Packages })
if ($packageLayers.Count -eq 0 -or $layeredPackages.Count -ne $packages.Count) {
    throw 'Release package dependency layering does not cover the manifest exactly once.'
}
for ($layerIndex = 0; $layerIndex -lt $packageLayers.Count; $layerIndex++) {
    if ($packageLayers[$layerIndex].Depth -ne $layerIndex -or $packageLayers[$layerIndex].Packages.Count -eq 0) {
        throw 'Release package dependency layers are not contiguous and non-empty.'
    }
}

Add-Type -AssemblyName System.IO.Compression
$temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('opengameagent-release-script-test-' + [Guid]::NewGuid().ToString('N'))
try {
    New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
    $localPackage = Join-Path $temporaryRoot 'local.nupkg'
    $changedPackage = Join-Path $temporaryRoot 'changed.nupkg'
    foreach ($packageFixture in @(
        [pscustomobject]@{ Path = $localPackage; Content = 'original' },
        [pscustomobject]@{ Path = $changedPackage; Content = 'changed' }
    )) {
        $archive = [IO.Compression.ZipFile]::Open(
            $packageFixture.Path,
            [IO.Compression.ZipArchiveMode]::Create)
        try {
            $entry = $archive.CreateEntry('lib/net8.0/Test.dll')
            $entryStream = $entry.Open()
            try {
                $writer = [IO.StreamWriter]::new($entryStream, [Text.UTF8Encoding]::new($false))
                try {
                    $writer.Write($packageFixture.Content)
                }
                finally {
                    $writer.Dispose()
                }
            }
            finally {
                $entryStream.Dispose()
            }
        }
        finally {
            $archive.Dispose()
        }
    }

    $localHash = [Convert]::FromHexString(
        (Get-FileHash -LiteralPath $localPackage -Algorithm SHA256).Hash)
    Assert-UnsignedNuGetPackageContentHash `
        -PackagePath $localPackage `
        -Algorithm SHA256 `
        -ExpectedHash $localHash

    $differentContentRejected = $false
    try {
        Assert-UnsignedNuGetPackageContentHash `
            -PackagePath $changedPackage `
            -Algorithm SHA256 `
            -ExpectedHash $localHash
    }
    catch {
        $differentContentRejected = $true
    }
    if (-not $differentContentRejected) {
        throw 'A partial NuGet publish retry accepted different package content.'
    }

    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    Add-Type -AssemblyName System.Formats.Asn1
    $properties = [Text.Encoding]::UTF8.GetBytes(
        "Version:1`n`n2.16.840.1.101.3.4.2.1-Hash:$([Convert]::ToBase64String($localHash))`n")
    $contentInfo = [Security.Cryptography.Pkcs.ContentInfo]::new($properties)
    $signedCms = [Security.Cryptography.Pkcs.SignedCms]::new($contentInfo, $false)
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    try {
        $certificateRequest = [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=OpenGameAgent release fixture',
            $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1)
        $certificate = $certificateRequest.CreateSelfSigned(
            [DateTimeOffset]::UtcNow.AddMinutes(-1),
            [DateTimeOffset]::UtcNow.AddMinutes(10))
        try {
            $signer = [Security.Cryptography.Pkcs.CmsSigner]::new($certificate)
            $attributeWriter = [Formats.Asn1.AsnWriter]::new([Formats.Asn1.AsnEncodingRules]::DER)
            $attributeWriter.WriteCharacterString(
                [Formats.Asn1.UniversalTagNumber]::IA5String,
                'https://api.nuget.org/v3/index.json')
            $attributeOid = [Security.Cryptography.Oid]::new('1.3.6.1.4.1.311.84.2.1.1.1')
            $attributeValues = [Security.Cryptography.AsnEncodedDataCollection]::new()
            $null = $attributeValues.Add([Security.Cryptography.AsnEncodedData]::new(
                $attributeOid,
                $attributeWriter.Encode()))
            $null = $signer.SignedAttributes.Add(
                [Security.Cryptography.CryptographicAttributeObject]::new($attributeOid, $attributeValues))
            $signedCms.ComputeSignature($signer)
        }
        finally {
            $certificate.Dispose()
        }
    }
    finally {
        $rsa.Dispose()
    }

    $signedPackage = Join-Path $temporaryRoot 'repository-signed.nupkg'
    $signedArchive = [IO.Compression.ZipFile]::Open(
        $signedPackage,
        [IO.Compression.ZipArchiveMode]::Create)
    try {
        $signatureEntry = $signedArchive.CreateEntry(
            '.signature.p7s',
            [IO.Compression.CompressionLevel]::NoCompression)
        $signatureStream = $signatureEntry.Open()
        try {
            $signatureBytes = $signedCms.Encode()
            $signatureStream.Write($signatureBytes, 0, $signatureBytes.Length)
        }
        finally {
            $signatureStream.Dispose()
        }
    }
    finally {
        $signedArchive.Dispose()
    }
    $publishedHash = Get-NuGetRepositorySignedContentHash -PackagePath $signedPackage
    if ($publishedHash.Algorithm -ne 'SHA256' -or
        -not [Security.Cryptography.CryptographicOperations]::FixedTimeEquals(
            $publishedHash.HashBytes,
            $localHash)) {
        throw 'Repository-signed NuGet content hash parsing failed.'
    }
}
finally {
    if (Test-Path -LiteralPath $temporaryRoot) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}

Write-Output "Release script checks passed for $($packages.Count) topologically ordered packages."
