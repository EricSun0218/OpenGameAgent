[CmdletBinding()]
param(
    [Parameter(Mandatory, Position = 0)]
    [string[]]$Path,

    [Alias('DenyRegex')]
    [string[]]$DeniedRegex = @()
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$maximumItemBytes = 268435456L
$maximumArchiveBytes = 1073741824L
$regexTimeout = [TimeSpan]::FromSeconds(1)
$regexOptions = (
    [Text.RegularExpressions.RegexOptions]::CultureInvariant -bor
    [Text.RegularExpressions.RegexOptions]::IgnoreCase -bor
    [Text.RegularExpressions.RegexOptions]::Multiline)
$repositoryRoot = [IO.Path]::GetFullPath(
    (Join-Path $PSScriptRoot '..\..'))
$binaryEncoding = [Text.Encoding]::GetEncoding(28591)

function Add-PrivatePathForms {
    param(
        [Parameter(Mandatory)]
        [AllowEmptyCollection()]
        [Collections.Generic.List[string]]$Destination,

        [string]$Value
    )

    if ([string]::IsNullOrWhiteSpace($Value)) {
        return
    }

    $fullPath = [IO.Path]::GetFullPath($Value).TrimEnd(
        [char[]]@(
            [IO.Path]::DirectorySeparatorChar,
            [IO.Path]::AltDirectorySeparatorChar))
    if ($fullPath.Length -lt 4) {
        return
    }

    $forward = $fullPath.Replace('\', '/').TrimEnd('/')
    $backward = $fullPath.Replace('/', '\').TrimEnd('\')
    foreach ($form in @(
            ($forward + '/'),
            ($backward + '\'))) {
        if (-not $Destination.Contains($form)) {
            $Destination.Add($form)
        }
    }
}

function New-BoundedRegex {
    param(
        [Parameter(Mandatory)]
        [string]$Pattern,

        [Parameter(Mandatory)]
        [string]$FailureMessage
    )

    try {
        return [regex]::new(
            $Pattern,
            $script:regexOptions,
            $script:regexTimeout)
    }
    catch {
        throw $FailureMessage
    }
}

$privatePathForms = New-Object 'Collections.Generic.List[string]'
Add-PrivatePathForms -Destination $privatePathForms -Value $repositoryRoot
$userProfile = [Environment]::GetFolderPath(
    [Environment+SpecialFolder]::UserProfile)
if (-not [string]::Equals(
        $userProfile,
        '/root',
        [StringComparison]::Ordinal)) {
    Add-PrivatePathForms `
        -Destination $privatePathForms `
        -Value $userProfile
}

$privatePathRegexes = @(
    (New-BoundedRegex `
        -Pattern '[a-z]:[\\/]+users[\\/]+[^\x00\r\n\\/]{1,128}[\\/]' `
        -FailureMessage 'The release path scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '/(?:home|users)/[^/\x00\r\n]{1,128}/' `
        -FailureMessage 'The release path scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '/root/(?:work|documents|source|src|workspace)/' `
        -FailureMessage 'The release path scanner could not be initialized.'))

$credentialRegexes = @(
    (New-BoundedRegex `
        -Pattern '-----BEGIN (?:RSA |EC |OPENSSH )?PRIVATE KEY-----' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])sk-[a-z0-9_-]{20,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])github_pat_[a-z0-9_]{20,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])gh[pousr]_[a-z0-9]{20,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])(?:AKIA|ASIA)[0-9A-Z]{16}(?![0-9A-Z])' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])AIza[0-9A-Za-z_-]{30,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9_-])eyJ[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}\.[a-z0-9_-]{8,}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '\bBearer\s+[a-z0-9._~+/-]{20,}={0,2}' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '(?<![a-z0-9])["'']?(?:[a-z0-9]+[_-]+)*(?:api[_-]?key|access[_-]?token|client[_-]?secret|authorization)["'']?\s*[:=]\s*["''][a-z0-9_./+=-]{20,}["'']' `
        -FailureMessage 'The credential scanner could not be initialized.'),
    (New-BoundedRegex `
        -Pattern '^\s*(?:export\s+)?["'']?(?:[a-z0-9]+[_-]+)*(?:api[_-]?key|access[_-]?token|client[_-]?secret|authorization)["'']?\s*[:=]\s*[a-z0-9_./+=-]{20,}\s*(?:[#;].*)?$' `
        -FailureMessage 'The credential scanner could not be initialized.'))

$deniedPatterns = New-Object 'Collections.Generic.List[string]'
foreach ($pattern in $DeniedRegex) {
    if (-not [string]::IsNullOrWhiteSpace($pattern)) {
        $deniedPatterns.Add($pattern)
    }
}
$injectedPatterns = [Environment]::GetEnvironmentVariable(
    'GAME_AGENT_RELEASE_DENY_REGEX')
if (-not [string]::IsNullOrWhiteSpace($injectedPatterns)) {
    foreach ($pattern in ($injectedPatterns -split '\r?\n')) {
        if (-not [string]::IsNullOrWhiteSpace($pattern)) {
            $deniedPatterns.Add($pattern)
        }
    }
}
$deniedRegexes = @(
    foreach ($pattern in $deniedPatterns) {
        New-BoundedRegex `
            -Pattern $pattern `
            -FailureMessage (
                'An externally supplied release deny expression is invalid.')
    }
)

function Get-ArtifactItemId {
    param([Parameter(Mandatory)][string]$Label)

    $sha = [Security.Cryptography.SHA256]::Create()
    try {
        $bytes = [Text.Encoding]::UTF8.GetBytes($Label)
        $hash = $sha.ComputeHash($bytes)
    }
    finally {
        $sha.Dispose()
    }

    $hex = [BitConverter]::ToString($hash) -replace '-', ''
    return $hex.Substring(0, 12).ToLowerInvariant()
}

function Test-RegexMatch {
    param(
        [Parameter(Mandatory)]
        [regex]$Regex,

        [Parameter(Mandatory)]
        [string]$Value
    )

    try {
        return $Regex.IsMatch($Value)
    }
    catch [Text.RegularExpressions.RegexMatchTimeoutException] {
        throw 'A release scan expression exceeded its safety limit.'
    }
}

function Assert-SafeText {
    param(
        [Parameter(Mandatory)]
        [string]$Value,

        [Parameter(Mandatory)]
        [string]$ItemId
    )

    foreach ($privatePath in $script:privatePathForms) {
        if ($Value.IndexOf(
                $privatePath,
                [StringComparison]::OrdinalIgnoreCase) -ge 0) {
            throw "Release artifact item '$ItemId' contains a private build path."
        }
    }

    foreach ($pathRegex in $script:privatePathRegexes) {
        if (Test-RegexMatch -Regex $pathRegex -Value $Value) {
            throw "Release artifact item '$ItemId' contains a local user path."
        }
    }

    foreach ($credentialRegex in $script:credentialRegexes) {
        if (Test-RegexMatch -Regex $credentialRegex -Value $Value) {
            throw "Release artifact item '$ItemId' contains credential-like data."
        }
    }

    foreach ($deniedRegex in $script:deniedRegexes) {
        if (Test-RegexMatch -Regex $deniedRegex -Value $Value) {
            throw "Release artifact item '$ItemId' contains denied release data."
        }
    }
}

function Assert-ArtifactName {
    param([Parameter(Mandatory)][string]$Name)

    $itemId = Get-ArtifactItemId -Label $Name
    if ($Name.Length -gt 4096 -or $Name.IndexOf([char]0) -ge 0) {
        throw "Release artifact item '$ItemId' has an unsafe name."
    }

    $normalized = $Name.Replace('\', '/')
    $segments = @($normalized.Split('/') | Where-Object { $_.Length -gt 0 })
    if ($normalized.StartsWith('/', [StringComparison]::Ordinal) -or
        $normalized -match '^[a-z]:' -or
        $segments -contains '..') {
        throw "Release artifact item '$ItemId' has an unsafe rooted or traversal name."
    }

    Assert-SafeText -Value $Name -ItemId $itemId
}

function Assert-ArtifactBytes {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [string]$Label
    )

    $itemId = Get-ArtifactItemId -Label $Label
    $views = @(
        $script:binaryEncoding.GetString($Bytes),
        [Text.Encoding]::UTF8.GetString($Bytes),
        [Text.Encoding]::Unicode.GetString($Bytes),
        [Text.Encoding]::BigEndianUnicode.GetString($Bytes),
        [Text.Encoding]::UTF32.GetString($Bytes))
    foreach ($view in $views) {
        Assert-SafeText -Value $view -ItemId $itemId
    }
}

function Test-Archive {
    param([Parameter(Mandatory)][string]$ArchivePath)

    Add-Type -AssemblyName System.IO.Compression.FileSystem
    $archive = [IO.Compression.ZipFile]::OpenRead($ArchivePath)
    $expandedBytes = 0L
    try {
        foreach ($entry in $archive.Entries) {
            Assert-ArtifactName -Name $entry.FullName
            if ($entry.Length -gt $script:maximumItemBytes) {
                throw 'A release archive entry exceeds the privacy scanner limit.'
            }

            $expandedBytes += [int64]$entry.Length
            if ($expandedBytes -gt $script:maximumArchiveBytes) {
                throw 'The expanded release archive exceeds the privacy scanner limit.'
            }
            if ($entry.Length -eq 0) {
                continue
            }

            $stream = $entry.Open()
            $memory = New-Object IO.MemoryStream
            try {
                $stream.CopyTo($memory)
                Assert-ArtifactBytes `
                    -Bytes $memory.ToArray() `
                    -Label $entry.FullName
            }
            finally {
                $memory.Dispose()
                $stream.Dispose()
            }
        }
    }
    finally {
        $archive.Dispose()
    }
}

$files = New-Object 'Collections.Generic.List[object]'
foreach ($inputPath in $Path) {
    $resolved = Resolve-Path -LiteralPath $inputPath
    $item = Get-Item -LiteralPath $resolved
    if (($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'Release inputs must not be filesystem links.'
    }

    if ($item.PSIsContainer) {
        $root = [IO.Path]::GetFullPath($item.FullName).TrimEnd(
            [char[]]@(
                [IO.Path]::DirectorySeparatorChar,
                [IO.Path]::AltDirectorySeparatorChar))
        $prefixLength = $root.Length + 1
        foreach ($child in Get-ChildItem -LiteralPath $root -Recurse -Force) {
            if (($child.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) {
                throw 'Release inputs must not contain filesystem links.'
            }

            $fullName = [IO.Path]::GetFullPath($child.FullName)
            $relativeName = $fullName.Substring($prefixLength)
            Assert-ArtifactName -Name $relativeName
            if (-not $child.PSIsContainer) {
                $files.Add([pscustomobject]@{
                        File = $child
                        Label = $relativeName
                    })
            }
        }
    }
    else {
        Assert-ArtifactName -Name $item.Name
        $files.Add([pscustomobject]@{
                File = $item
                Label = $item.Name
            })
    }
}

foreach ($record in $files) {
    if ($record.File.Length -gt $maximumItemBytes) {
        throw 'A release file exceeds the privacy scanner limit.'
    }

    $bytes = [IO.File]::ReadAllBytes($record.File.FullName)
    Assert-ArtifactBytes -Bytes $bytes -Label $record.Label

    $extension = $record.File.Extension.ToLowerInvariant()
    if ($extension -in @('.zip', '.nupkg', '.snupkg')) {
        Test-Archive -ArchivePath $record.File.FullName
    }
}

Write-Output 'RELEASE_ARTIFACT_PRIVACY_PASS'
