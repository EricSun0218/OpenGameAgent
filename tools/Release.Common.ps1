function Get-ReleaseVersionInfo {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Version
    )

    if ([string]::IsNullOrWhiteSpace($Version) -or $Version.Length -gt 64) {
        throw 'Release version must contain between 1 and 64 characters.'
    }

    if ($Version.Contains('+')) {
        throw 'Release versions cannot contain build metadata because NuGet removes it from package identity.'
    }

    $match = [regex]::Match(
        $Version,
        '^(?<major>0|[1-9][0-9]*)\.(?<minor>0|[1-9][0-9]*)\.(?<patch>0|[1-9][0-9]*)(?:-(?<prerelease>[0-9A-Za-z-]+(?:\.[0-9A-Za-z-]+)*))?$')
    if (-not $match.Success) {
        throw 'Release version must be a canonical SemVer version without build metadata.'
    }

    [int] $major = 0
    [int] $minor = 0
    [int] $patch = 0
    foreach ($numericComponent in @(
        [pscustomobject]@{ Name = 'major'; Value = $match.Groups['major'].Value; Target = [ref]$major },
        [pscustomobject]@{ Name = 'minor'; Value = $match.Groups['minor'].Value; Target = [ref]$minor },
        [pscustomobject]@{ Name = 'patch'; Value = $match.Groups['patch'].Value; Target = [ref]$patch }
    )) {
        if (-not [int]::TryParse(
            $numericComponent.Value,
            [Globalization.NumberStyles]::None,
            [Globalization.CultureInfo]::InvariantCulture,
            $numericComponent.Target)) {
            throw "Release version $($numericComponent.Name) component exceeds NuGet's Int32 range."
        }
    }

    $prerelease = $match.Groups['prerelease'].Value
    if (-not [string]::IsNullOrEmpty($prerelease)) {
        foreach ($identifier in $prerelease.Split('.')) {
            if ($identifier -match '^[0-9]+$' -and $identifier.Length -gt 1 -and $identifier[0] -eq '0') {
                throw 'Numeric prerelease identifiers cannot contain leading zeroes.'
            }
        }
    }

    [pscustomobject]@{
        Version = $Version
        Major = $major
        Minor = $minor
        Patch = $patch
        IsPrerelease = -not [string]::IsNullOrEmpty($prerelease)
        FlatContainerVersion = $Version.ToLowerInvariant()
    }
}

function Get-ReleaseStabilityNotice {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object] $VersionInfo
    )

    if ($VersionInfo.IsPrerelease) {
        return 'This is a pre-release. Public APIs may change before the final release.'
    }
    if ([int]$VersionInfo.Major -eq 0) {
        return 'This is a stable-version release. Before 1.0, minor versions can still change public APIs.'
    }

    return 'This is a stable release governed by semantic-versioning compatibility guarantees.'
}

function Get-NuGetRepositorySignedContentHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackagePath,
        [Uri] $ExpectedServiceIndexUri = 'https://api.nuget.org/v3/index.json'
    )

    $resolvedPath = [IO.Path]::GetFullPath($PackagePath)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Published NuGet package '$resolvedPath' does not exist."
    }
    $packageLength = (Get-Item -LiteralPath $resolvedPath).Length
    if ($packageLength -le 0 -or $packageLength -gt 314572800) {
        throw 'Published NuGet package size is outside the accepted release bounds.'
    }
    if ($null -eq $ExpectedServiceIndexUri -or
        -not $ExpectedServiceIndexUri.IsAbsoluteUri -or
        $ExpectedServiceIndexUri.Scheme -ne 'https') {
        throw 'An absolute HTTPS NuGet service index URI is required.'
    }

    Add-Type -AssemblyName System.IO.Compression
    Add-Type -AssemblyName System.Security.Cryptography.Pkcs
    Add-Type -AssemblyName System.Formats.Asn1

    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedPath)
    try {
        $signatureEntries = @($archive.Entries | Where-Object { $_.FullName -ceq '.signature.p7s' })
        $ambiguousSignatureEntries = @($archive.Entries | Where-Object {
            $_.FullName -ieq '.signature.p7s' -and $_.FullName -cne '.signature.p7s'
        })
        if ($signatureEntries.Count -ne 1 -or $ambiguousSignatureEntries.Count -ne 0) {
            throw 'Published NuGet package must contain exactly one canonical signature entry.'
        }

        $signatureEntry = $signatureEntries[0]
        if ($signatureEntry.Length -le 0 -or
            $signatureEntry.Length -gt 1048576 -or
            $signatureEntry.CompressedLength -ne $signatureEntry.Length) {
            throw 'Published NuGet package signature entry is invalid or exceeds its size bound.'
        }

        $signatureStream = $signatureEntry.Open()
        try {
            $signatureBuffer = [IO.MemoryStream]::new([int]$signatureEntry.Length)
            try {
                $signatureStream.CopyTo($signatureBuffer)
                $signatureBytes = $signatureBuffer.ToArray()
            }
            finally {
                $signatureBuffer.Dispose()
            }
        }
        finally {
            $signatureStream.Dispose()
        }
    }
    finally {
        $archive.Dispose()
    }

    try {
        $signedCms = [Security.Cryptography.Pkcs.SignedCms]::new()
        $signedCms.Decode($signatureBytes)
        $signedCms.CheckSignature($true)
    }
    catch {
        throw 'Published NuGet package signature is malformed or cryptographically invalid.'
    }
    if ($signedCms.SignerInfos.Count -ne 1) {
        throw 'Published NuGet package must contain exactly one primary signer.'
    }

    $primarySigner = $signedCms.SignerInfos[0]
    $repositoryAttributes = @($primarySigner.SignedAttributes | Where-Object {
        $_.Oid.Value -eq '1.3.6.1.4.1.311.84.2.1.1.1'
    })
    if ($repositoryAttributes.Count -ne 1 -or $repositoryAttributes[0].Values.Count -ne 1) {
        throw 'Published NuGet package does not contain a unique primary repository signature.'
    }
    try {
        $attributeReader = [Formats.Asn1.AsnReader]::new(
            $repositoryAttributes[0].Values[0].RawData,
            [Formats.Asn1.AsnEncodingRules]::DER)
        $repositoryServiceIndex = $attributeReader.ReadCharacterString(
            [Formats.Asn1.UniversalTagNumber]::IA5String)
        $attributeReader.ThrowIfNotEmpty()
    }
    catch {
        throw 'Published NuGet package repository signature metadata is malformed.'
    }
    if (-not [string]::Equals(
        $repositoryServiceIndex,
        $ExpectedServiceIndexUri.AbsoluteUri,
        [StringComparison]::OrdinalIgnoreCase)) {
        throw "Published NuGet package was signed by an unexpected repository '$repositoryServiceIndex'."
    }

    try {
        $strictUtf8 = [Text.UTF8Encoding]::new($false, $true)
        $signatureContent = $strictUtf8.GetString($signedCms.ContentInfo.Content)
    }
    catch {
        throw 'Published NuGet package signature content is not valid UTF-8.'
    }
    if ($signatureContent.Contains("`0") -or
        $signatureContent -match "`r(?!`n)" -or
        $signatureContent -notmatch '\AVersion:1(?:\r?\n){2}') {
        throw 'Published NuGet package signature content has an invalid properties document.'
    }

    $hashMatches = [regex]::Matches(
        $signatureContent,
        '(?m)^(?<oid>2\.16\.840\.1\.101\.3\.4\.2\.[123])-Hash:(?<value>[A-Za-z0-9+/]+={0,2})\r?$')
    if ($hashMatches.Count -ne 1) {
        throw 'Published NuGet package signature must contain exactly one supported content hash.'
    }

    $hashMetadata = switch ($hashMatches[0].Groups['oid'].Value) {
        '2.16.840.1.101.3.4.2.1' { [pscustomobject]@{ Algorithm = 'SHA256'; Length = 32 } }
        '2.16.840.1.101.3.4.2.2' { [pscustomobject]@{ Algorithm = 'SHA384'; Length = 48 } }
        '2.16.840.1.101.3.4.2.3' { [pscustomobject]@{ Algorithm = 'SHA512'; Length = 64 } }
        default { throw 'Published NuGet package uses an unsupported content hash algorithm.' }
    }
    try {
        $hashBytes = [Convert]::FromBase64String($hashMatches[0].Groups['value'].Value)
    }
    catch {
        throw 'Published NuGet package signature content hash is malformed.'
    }
    if ($hashBytes.Length -ne $hashMetadata.Length) {
        throw 'Published NuGet package signature content hash has an invalid length.'
    }

    return [pscustomobject]@{
        Algorithm = $hashMetadata.Algorithm
        HashBytes = $hashBytes
        HashBase64 = [Convert]::ToBase64String($hashBytes)
    }
}

function Test-UnsignedNuGetPackageContentHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackagePath,
        [Parameter(Mandatory = $true)]
        [ValidateSet('SHA256', 'SHA384', 'SHA512')]
        [string] $Algorithm,
        [Parameter(Mandatory = $true)]
        [byte[]] $ExpectedHash
    )

    $resolvedPath = [IO.Path]::GetFullPath($PackagePath)
    if (-not (Test-Path -LiteralPath $resolvedPath -PathType Leaf)) {
        throw "Local NuGet package '$resolvedPath' does not exist."
    }
    $packageLength = (Get-Item -LiteralPath $resolvedPath).Length
    if ($packageLength -le 0 -or $packageLength -gt 314572800) {
        throw 'Local NuGet package size is outside the accepted release bounds.'
    }

    Add-Type -AssemblyName System.IO.Compression
    $archive = [IO.Compression.ZipFile]::OpenRead($resolvedPath)
    try {
        $signatureEntries = @($archive.Entries | Where-Object { $_.FullName -ieq '.signature.p7s' })
        if ($signatureEntries.Count -ne 0) {
            throw 'Release retry verification currently requires the local NuGet package to be unsigned.'
        }
    }
    finally {
        $archive.Dispose()
    }

    $expectedLength = switch ($Algorithm) {
        'SHA256' { 32 }
        'SHA384' { 48 }
        'SHA512' { 64 }
    }
    if ($null -eq $ExpectedHash -or $ExpectedHash.Length -ne $expectedLength) {
        throw "Expected $Algorithm content hash has an invalid length."
    }

    $localHashHex = (Get-FileHash -LiteralPath $resolvedPath -Algorithm $Algorithm).Hash
    $localHash = [Convert]::FromHexString($localHashHex)
    return [Security.Cryptography.CryptographicOperations]::FixedTimeEquals($localHash, $ExpectedHash)
}

function Assert-UnsignedNuGetPackageContentHash {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PackagePath,
        [Parameter(Mandatory = $true)]
        [ValidateSet('SHA256', 'SHA384', 'SHA512')]
        [string] $Algorithm,
        [Parameter(Mandatory = $true)]
        [byte[]] $ExpectedHash
    )

    if (-not (Test-UnsignedNuGetPackageContentHash `
        -PackagePath $PackagePath `
        -Algorithm $Algorithm `
        -ExpectedHash $ExpectedHash)) {
        throw 'Published NuGet package content does not match the local release package.'
    }
}

function Get-ReleasePackageManifest {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $manifestPath = Join-Path $root 'tools/release-packages.json'
    if (-not (Test-Path -LiteralPath $manifestPath -PathType Leaf)) {
        throw "Release package manifest '$manifestPath' does not exist."
    }

    $document = Get-Content -LiteralPath $manifestPath -Raw | ConvertFrom-Json
    if ($document.schemaVersion -ne 1) {
        throw 'Release package manifest has an unsupported schema version.'
    }

    $packages = @($document.packages)
    if ($packages.Count -eq 0) {
        throw 'Release package manifest cannot be empty.'
    }

    $ids = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $paths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($package in $packages) {
        $id = [string]$package.id
        $project = [string]$package.project
        if ($id -notmatch '^[A-Za-z0-9_.-]{1,100}$') {
            throw "Release package id '$id' is invalid."
        }
        if (-not $ids.Add($id)) {
            throw "Release package id '$id' is duplicated."
        }
        if ([string]::IsNullOrWhiteSpace($project) -or [IO.Path]::IsPathRooted($project)) {
            throw "Release project path '$project' must be repository-relative."
        }

        $portableProject = $project.Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar)
        $fullProject = [IO.Path]::GetFullPath((Join-Path $root $portableProject))
        if (-not $fullProject.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Release project path '$project' escapes the repository."
        }
        if (-not (Test-Path -LiteralPath $fullProject -PathType Leaf)) {
            throw "Release project '$project' does not exist."
        }
        if (-not $paths.Add($fullProject)) {
            throw "Release project '$project' is duplicated."
        }

        Add-Member -InputObject $package -NotePropertyName FullProjectPath -NotePropertyValue $fullProject -Force
    }

    return $packages
}

function Assert-ReleasePackageManifestGraph {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $RepositoryRoot,
        [Parameter(Mandatory = $true)]
        [object[]] $Packages
    )

    $root = [IO.Path]::GetFullPath($RepositoryRoot)
    $sourceRoot = Join-Path $root 'src'
    $packableProjects = @{}
    foreach ($projectFile in Get-ChildItem -LiteralPath $sourceRoot -Recurse -Filter '*.csproj' -File) {
        [xml]$projectXml = Get-Content -LiteralPath $projectFile.FullName
        $isPackableValues = @($projectXml.SelectNodes('/Project/PropertyGroup/IsPackable'))
        if ($isPackableValues.Count -gt 0 -and [string]$isPackableValues[-1].InnerText -eq 'false') {
            continue
        }

        $packageIdValues = @($projectXml.SelectNodes('/Project/PropertyGroup/PackageId'))
        $packageId = if ($packageIdValues.Count -gt 0) { [string]$packageIdValues[-1].InnerText } else { $projectFile.BaseName }
        $packableProjects[[IO.Path]::GetFullPath($projectFile.FullName)] = $packageId
    }

    $manifestPaths = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $manifestIds = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $positionByPath = @{}
    for ($index = 0; $index -lt $Packages.Count; $index++) {
        $package = $Packages[$index]
        $path = [IO.Path]::GetFullPath([string]$package.FullProjectPath)
        $null = $manifestPaths.Add($path)
        $null = $manifestIds.Add([string]$package.id)
        $positionByPath[$path] = $index

        if (-not $packableProjects.ContainsKey($path)) {
            throw "Release manifest project '$($package.project)' is not a packable source project."
        }
        if (-not [string]::Equals($packableProjects[$path], [string]$package.id, [StringComparison]::Ordinal)) {
            throw "Release package id '$($package.id)' does not match project package id '$($packableProjects[$path])'."
        }
    }

    foreach ($projectPath in $packableProjects.Keys) {
        if (-not $manifestPaths.Contains($projectPath)) {
            throw "Packable project '$projectPath' is missing from the release manifest."
        }
    }
    if ($manifestPaths.Count -ne $packableProjects.Count -or $manifestIds.Count -ne $packableProjects.Count) {
        throw 'Release package manifest does not map one-to-one to packable source projects.'
    }

    foreach ($package in $Packages) {
        $projectPath = [IO.Path]::GetFullPath([string]$package.FullProjectPath)
        [xml]$projectXml = Get-Content -LiteralPath $projectPath
        foreach ($reference in @($projectXml.SelectNodes('/Project/ItemGroup/ProjectReference'))) {
            if ([string]::IsNullOrWhiteSpace([string]$reference.Include)) {
                continue
            }

            $portableReference = ([string]$reference.Include).Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar)
            $dependencyPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $projectPath) $portableReference))
            if (-not $positionByPath.ContainsKey($dependencyPath)) {
                continue
            }
            if ($positionByPath[$dependencyPath] -ge $positionByPath[$projectPath]) {
                throw "Release package '$($package.id)' appears before dependency '$($packableProjects[$dependencyPath])'."
            }
        }
    }
}

function Get-ReleasePackageLayers {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [object[]] $Packages
    )

    $packageByPath = @{}
    foreach ($package in $Packages) {
        $packageByPath[[IO.Path]::GetFullPath([string]$package.FullProjectPath)] = $package
    }

    $depthByPath = @{}
    $groups = [Collections.Generic.SortedDictionary[int,Collections.Generic.List[object]]]::new()
    foreach ($package in $Packages) {
        $projectPath = [IO.Path]::GetFullPath([string]$package.FullProjectPath)
        [xml]$projectXml = Get-Content -LiteralPath $projectPath
        [int] $depth = 0
        foreach ($reference in @($projectXml.SelectNodes('/Project/ItemGroup/ProjectReference'))) {
            if ([string]::IsNullOrWhiteSpace([string]$reference.Include)) {
                continue
            }
            $portableReference = ([string]$reference.Include).Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar)
            $dependencyPath = [IO.Path]::GetFullPath((Join-Path (Split-Path -Parent $projectPath) $portableReference))
            if (-not $packageByPath.ContainsKey($dependencyPath)) {
                continue
            }
            if (-not $depthByPath.ContainsKey($dependencyPath)) {
                throw "Release package '$($package.id)' appears before one of its dependencies."
            }
            $depth = [Math]::Max($depth, [int]$depthByPath[$dependencyPath] + 1)
        }
        $depthByPath[$projectPath] = $depth
        if (-not $groups.ContainsKey($depth)) {
            $groups.Add($depth, [Collections.Generic.List[object]]::new())
        }
        $groups[$depth].Add($package)
    }

    return @($groups.GetEnumerator() | ForEach-Object {
        [pscustomobject]@{
            Depth = [int]$_.Key
            Packages = [object[]]$_.Value.ToArray()
        }
    })
}

function Resolve-PortableServerRuntimeAssets {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $PublishDirectory,
        [Parameter(Mandatory = $true)]
        [string] $DepsFile
    )

    $publishRoot = [IO.Path]::GetFullPath($PublishDirectory)
    $depsPath = [IO.Path]::GetFullPath($DepsFile)
    if (-not (Test-Path -LiteralPath $publishRoot -PathType Container)) {
        throw "Server publish directory '$publishRoot' does not exist."
    }
    if (-not (Test-Path -LiteralPath $depsPath -PathType Leaf)) {
        throw "Server dependency manifest '$depsPath' does not exist."
    }

    $document = Get-Content -LiteralPath $depsPath -Raw | ConvertFrom-Json
    $targets = @($document.targets.PSObject.Properties)
    if ($targets.Count -ne 1) {
        throw 'Portable server dependency manifest must contain exactly one runtime target.'
    }

    $declaredAssets = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($library in $targets[0].Value.PSObject.Properties) {
        foreach ($sectionName in @('runtime', 'native', 'resources', 'runtimeTargets')) {
            $section = $library.Value.PSObject.Properties[$sectionName]
            if ($null -eq $section) {
                continue
            }
            foreach ($assetName in $section.Value.PSObject.Properties.Name) {
                if ([IO.Path]::GetFileName([string]$assetName) -eq '_._') {
                    continue
                }
                $null = $declaredAssets.Add([string]$assetName)
            }
        }
    }
    if ($declaredAssets.Count -eq 0) {
        throw 'Portable server dependency manifest contains no runtime assets.'
    }

    $resolvedByDestination = @{}
    foreach ($declaredAsset in $declaredAssets) {
        if ([IO.Path]::IsPathRooted($declaredAsset)) {
            throw "Runtime asset '$declaredAsset' must be relative."
        }
        $segments = $declaredAsset.Replace('\', '/').Split('/', [StringSplitOptions]::RemoveEmptyEntries)
        if ($segments -contains '..') {
            throw "Runtime asset '$declaredAsset' contains a parent traversal."
        }

        $portableAsset = $declaredAsset.Replace('/', [IO.Path]::DirectorySeparatorChar).Replace('\', [IO.Path]::DirectorySeparatorChar)
        $candidates = [Collections.Generic.List[string]]::new()
        $candidates.Add([IO.Path]::GetFullPath((Join-Path $publishRoot $portableAsset)))
        $leaf = [IO.Path]::GetFileName($portableAsset)
        $leafCandidate = [IO.Path]::GetFullPath((Join-Path $publishRoot $leaf))
        if (-not $candidates.Contains($leafCandidate)) {
            $candidates.Add($leafCandidate)
        }

        $matches = @($candidates | Where-Object {
            $_.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase) -and
            (Test-Path -LiteralPath $_ -PathType Leaf)
        } | Select-Object -Unique)
        if ($matches.Count -eq 0) {
            $matches = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object Name -eq $leaf | Select-Object -ExpandProperty FullName)
        }
        if ($matches.Count -ne 1) {
            throw "Runtime asset '$declaredAsset' resolved to $($matches.Count) published files."
        }

        $sourcePath = [IO.Path]::GetFullPath($matches[0])
        if (-not $sourcePath.StartsWith($publishRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Runtime asset '$declaredAsset' escapes the publish directory."
        }
        $destination = [IO.Path]::GetRelativePath($publishRoot, $sourcePath)
        if ($resolvedByDestination.ContainsKey($destination) -and
            -not [string]::Equals($resolvedByDestination[$destination], $sourcePath, [StringComparison]::OrdinalIgnoreCase)) {
            throw "Multiple runtime assets map to '$destination'."
        }
        $resolvedByDestination[$destination] = $sourcePath
    }

    $resolvedSources = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($source in $resolvedByDestination.Values) {
        $null = $resolvedSources.Add([IO.Path]::GetFullPath($source))
    }
    $publishedRuntimeFiles = @(Get-ChildItem -LiteralPath $publishRoot -Recurse -File | Where-Object {
        $_.Extension -in @('.dll', '.so', '.dylib')
    })
    foreach ($publishedRuntimeFile in $publishedRuntimeFiles) {
        if (-not $resolvedSources.Contains([IO.Path]::GetFullPath($publishedRuntimeFile.FullName))) {
            throw "Published runtime file '$($publishedRuntimeFile.FullName)' is absent from the dependency manifest."
        }
    }

    return @($resolvedByDestination.GetEnumerator() | Sort-Object Key | ForEach-Object {
        [pscustomobject]@{
            Source = [string]$_.Value
            Destination = [string]$_.Key
        }
    })
}

function Test-PortableServerArchive {
    [CmdletBinding()]
    param(
        [Parameter(Mandatory = $true)]
        [string] $Archive,
        [Parameter(Mandatory = $true)]
        [string] $EntryDirectoryName,
        [int] $TimeoutSeconds = 20
    )

    if ($TimeoutSeconds -lt 5 -or $TimeoutSeconds -gt 60) {
        throw 'Portable server smoke timeout must be between 5 and 60 seconds.'
    }
    if (-not (Test-Path -LiteralPath $Archive -PathType Leaf)) {
        throw "Portable server archive '$Archive' does not exist."
    }

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $temporaryRoot = Join-Path ([IO.Path]::GetTempPath()) ('opengameagent-server-smoke-' + [Guid]::NewGuid().ToString('N'))
    $process = $null
    $client = $null
    $failure = $null
    $standardOutput = ''
    $standardError = ''
    try {
        New-Item -ItemType Directory -Path $temporaryRoot | Out-Null
        [IO.Compression.ZipFile]::ExtractToDirectory([IO.Path]::GetFullPath($Archive), $temporaryRoot)
        $serverRoot = Join-Path $temporaryRoot $EntryDirectoryName
        $serverAssembly = Join-Path $serverRoot 'OpenGameAgent.Server.dll'
        if (-not (Test-Path -LiteralPath $serverAssembly -PathType Leaf)) {
            throw 'Portable server archive does not contain the server assembly at its documented path.'
        }

        $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
        try {
            $listener.Start()
            $port = ([Net.IPEndPoint]$listener.LocalEndpoint).Port
        }
        finally {
            $listener.Stop()
        }

        $startInfo = [Diagnostics.ProcessStartInfo]::new()
        $startInfo.FileName = (Get-Command dotnet -ErrorAction Stop).Source
        $startInfo.WorkingDirectory = $serverRoot
        $startInfo.ArgumentList.Add('OpenGameAgent.Server.dll')
        $startInfo.UseShellExecute = $false
        $startInfo.RedirectStandardOutput = $true
        $startInfo.RedirectStandardError = $true
        $startInfo.Environment['ASPNETCORE_URLS'] = "http://127.0.0.1:$port"
        $startInfo.Environment['DOTNET_NOLOGO'] = '1'
        $process = [Diagnostics.Process]::new()
        $process.StartInfo = $startInfo
        if (-not $process.Start()) {
            throw 'Portable server process did not start.'
        }

        $client = [Net.Http.HttpClient]::new()
        $client.Timeout = [TimeSpan]::FromSeconds(1)
        $deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
        $healthy = $false
        while ([DateTimeOffset]::UtcNow -lt $deadline) {
            if ($process.HasExited) {
                $failure = "Portable server exited before becoming healthy with code $($process.ExitCode)."
                break
            }
            try {
                $response = $client.GetAsync("http://127.0.0.1:$port/healthz").GetAwaiter().GetResult()
                try {
                    $body = $response.Content.ReadAsStringAsync().GetAwaiter().GetResult()
                    if ($response.IsSuccessStatusCode -and $body -match '"status"\s*:\s*"healthy"') {
                        $healthy = $true
                        break
                    }
                }
                finally {
                    $response.Dispose()
                }
            }
            catch [Net.Http.HttpRequestException] {
            }
            catch [Threading.Tasks.TaskCanceledException] {
            }
            Start-Sleep -Milliseconds 100
        }
        if (-not $healthy -and $null -eq $failure) {
            $failure = "Portable server did not become healthy within $TimeoutSeconds seconds."
        }
    }
    catch {
        $failure = $_.Exception.Message
    }
    finally {
        if ($null -ne $client) {
            $client.Dispose()
        }
        if ($null -ne $process) {
            try {
                if (-not $process.HasExited) {
                    $process.Kill($true)
                }
                if (-not $process.WaitForExit(5000)) {
                    if ($null -eq $failure) {
                        $failure = 'Portable server process did not exit during cleanup.'
                    }
                }
                else {
                    $standardOutput = $process.StandardOutput.ReadToEnd()
                    $standardError = $process.StandardError.ReadToEnd()
                }
            }
            catch {
                if ($null -eq $failure) {
                    $failure = 'Portable server process cleanup failed: ' + $_.Exception.Message
                }
            }
            finally {
                $process.Dispose()
            }
        }
        if (Test-Path -LiteralPath $temporaryRoot) {
            Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
        }
    }

    if ($null -ne $failure) {
        $logs = ($standardOutput + [Environment]::NewLine + $standardError).Trim()
        if ($logs.Length -gt 4000) {
            $logs = $logs.Substring(0, 4000)
        }
        throw ($failure + $(if ($logs.Length -gt 0) { [Environment]::NewLine + $logs } else { '' }))
    }
}
