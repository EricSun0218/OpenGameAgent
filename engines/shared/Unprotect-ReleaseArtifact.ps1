[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourcePath,

    [Parameter(Mandatory)]
    [string]$CertificatePath,

    [Parameter(Mandatory)]
    [string]$DestinationPath,

    [switch]$RequireInjectedKey,

    [long]$MaximumBytes = 67108864L
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Import-PublicCertificate {
    param([Parameter(Mandatory)][string]$Path)

    $certificateFile = Get-Item -LiteralPath (
        Resolve-Path -LiteralPath $Path)
    if ($certificateFile.PSIsContainer -or
        ($certificateFile.Attributes -band
            [IO.FileAttributes]::ReparsePoint) -ne 0) {
        throw 'The release artifact recipient must be a regular file.'
    }

    $pem = [IO.File]::ReadAllText($certificateFile.FullName)
    $match = [Text.RegularExpressions.Regex]::Match(
        $pem,
        (
            '\A-----BEGIN CERTIFICATE-----\s*' +
            '(?<body>[A-Za-z0-9+/=\s]+?)\s*' +
            '-----END CERTIFICATE-----\s*\z'))
    if (-not $match.Success) {
        throw 'The release artifact recipient is not a single PEM certificate.'
    }

    try {
        $encodedCertificate = (
            $match.Groups['body'].Value -replace '\s', '')
        $der = [Convert]::FromBase64String($encodedCertificate)
        return (
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $der))
    }
    catch {
        throw 'The release artifact recipient certificate is invalid.'
    }
}

function Copy-ByteRange {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Bytes,

        [Parameter(Mandatory)]
        [int]$Offset,

        [Parameter(Mandatory)]
        [int]$Length
    )

    if ($Offset -lt 0 -or $Length -lt 0 -or
        $Offset -gt ($Bytes.Length - $Length)) {
        throw 'The protected release artifact is truncated.'
    }
    $result = New-Object byte[] $Length
    [Buffer]::BlockCopy($Bytes, $Offset, $result, 0, $Length)
    return ,$result
}

if ($MaximumBytes -lt 1 -or $MaximumBytes -gt 134217728L) {
    throw 'The release artifact recovery limit is invalid.'
}
if ($null -eq ('Security.Cryptography.AesGcm' -as [type])) {
    throw 'Authenticated artifact recovery requires PowerShell 7.'
}

$source = Get-Item -LiteralPath (Resolve-Path -LiteralPath $SourcePath)
if ($source.PSIsContainer -or
    ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $source.Length -gt ($MaximumBytes + 4096L)) {
    throw 'The protected release artifact is unsafe or exceeds the limit.'
}

$destination = [IO.Path]::GetFullPath($DestinationPath)
if ($source.FullName.Equals(
        $destination,
        [StringComparison]::OrdinalIgnoreCase) -or
    [IO.File]::Exists($destination) -or
    [IO.Directory]::Exists($destination)) {
    throw 'The recovered release artifact destination is invalid.'
}
$destinationParent = Get-Item -LiteralPath (
    Split-Path -Parent $destination)
if (-not $destinationParent.PSIsContainer -or
    ($destinationParent.Attributes -band
        [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The recovered release artifact parent is unsafe.'
}

$injectedKey = [Environment]::GetEnvironmentVariable(
    'GAME_AGENT_ARTIFACT_DECRYPTION_PFX')
if ($RequireInjectedKey -and
    [string]::IsNullOrWhiteSpace($injectedKey)) {
    throw 'The required release artifact recovery key is unavailable.'
}
if ([string]::IsNullOrWhiteSpace($injectedKey) -or
    $injectedKey.Length -gt 65536) {
    throw 'The release artifact recovery key is invalid.'
}

$publicCertificate = Import-PublicCertificate -Path $CertificatePath
$privateCertificate = $null
$privateKey = $null
$aead = $null
$aesKey = $null
$plaintext = $null
$temporaryPath = (
    $destination + '.tmp.' + [guid]::NewGuid().ToString('N'))
try {
    try {
        $pfx = [Convert]::FromBase64String($injectedKey)
        $flags = (
            [Security.Cryptography.X509Certificates.X509KeyStorageFlags]::
                EphemeralKeySet)
        $privateCertificate = (
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $pfx,
                '',
                $flags))
    }
    catch {
        throw 'The release artifact recovery key cannot be loaded.'
    }

    if (-not $privateCertificate.HasPrivateKey -or
        -not $privateCertificate.Thumbprint.Equals(
            $publicCertificate.Thumbprint,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The release artifact recovery key does not match the recipient.'
    }

    $bytes = [IO.File]::ReadAllBytes($source.FullName)
    $magic = [Text.Encoding]::ASCII.GetBytes('GARSEALED1')
    $fixedHeaderLength = $magic.Length + 1 + 2 + 1 + 1 + 8
    if ($bytes.Length -lt ($fixedHeaderLength + 256 + 12 + 16)) {
        throw 'The protected release artifact is truncated.'
    }
    $header = Copy-ByteRange `
        -Bytes $bytes `
        -Offset 0 `
        -Length $fixedHeaderLength
    for ($index = 0; $index -lt $magic.Length; $index++) {
        if ($header[$index] -ne $magic[$index]) {
            throw 'The protected release artifact format is invalid.'
        }
    }

    $stream = [IO.MemoryStream]::new($header, $false)
    $reader = [IO.BinaryReader]::new(
        $stream,
        [Text.UTF8Encoding]::new($false),
        $false)
    try {
        $readMagic = $reader.ReadBytes($magic.Length)
        $version = $reader.ReadByte()
        $wrappedKeyLength = [int]$reader.ReadUInt16()
        $nonceLength = [int]$reader.ReadByte()
        $tagLength = [int]$reader.ReadByte()
        $plaintextLength = $reader.ReadInt64()
    }
    finally {
        $reader.Dispose()
    }
    if ($version -ne 1 -or
        $wrappedKeyLength -lt 256 -or
        $nonceLength -ne 12 -or
        $tagLength -ne 16 -or
        $plaintextLength -lt 0 -or
        $plaintextLength -gt $MaximumBytes) {
        throw 'The protected release artifact header is invalid.'
    }

    $expectedLength = (
        [int64]$fixedHeaderLength +
        $wrappedKeyLength +
        $nonceLength +
        $tagLength +
        $plaintextLength)
    if ($expectedLength -ne $bytes.LongLength) {
        throw 'The protected release artifact length is inconsistent.'
    }
    $offset = $fixedHeaderLength
    $wrappedKey = Copy-ByteRange `
        -Bytes $bytes -Offset $offset -Length $wrappedKeyLength
    $offset += $wrappedKeyLength
    $nonce = Copy-ByteRange `
        -Bytes $bytes -Offset $offset -Length $nonceLength
    $offset += $nonceLength
    $tag = Copy-ByteRange `
        -Bytes $bytes -Offset $offset -Length $tagLength
    $offset += $tagLength
    $ciphertext = Copy-ByteRange `
        -Bytes $bytes -Offset $offset -Length ([int]$plaintextLength)

    $privateKey = (
        [Security.Cryptography.X509Certificates.RSACertificateExtensions]::
            GetRSAPrivateKey($privateCertificate))
    if ($null -eq $privateKey -or
        $privateKey.ExportParameters($false).Modulus.Length -lt 256) {
        throw 'The release artifact recovery key is not a strong RSA key.'
    }
    try {
        $aesKey = $privateKey.Decrypt(
            $wrappedKey,
            [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
        if ($aesKey.Length -ne 32) {
            throw 'The recovered content key has an invalid length.'
        }
        $plaintext = New-Object byte[] ([int]$plaintextLength)
        $aead = [Security.Cryptography.AesGcm]::new($aesKey, 16)
        $aead.Decrypt($nonce, $ciphertext, $tag, $plaintext, $header)
    }
    catch [Security.Cryptography.CryptographicException] {
        throw 'The protected release artifact failed authentication.'
    }

    [IO.File]::WriteAllBytes($temporaryPath, $plaintext)
    if ([IO.File]::Exists($destination)) {
        throw 'The recovered release artifact destination already exists.'
    }
    [IO.File]::Move($temporaryPath, $destination)
}
finally {
    if ($null -ne $plaintext) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory(
            $plaintext)
    }
    if ($null -ne $aesKey) {
        [Security.Cryptography.CryptographicOperations]::ZeroMemory($aesKey)
    }
    if ($null -ne $aead) {
        $aead.Dispose()
    }
    if ($null -ne $privateKey) {
        $privateKey.Dispose()
    }
    if ($null -ne $privateCertificate) {
        $privateCertificate.Dispose()
    }
    $publicCertificate.Dispose()
    if ([IO.File]::Exists($temporaryPath)) {
        [IO.File]::Delete($temporaryPath)
    }
}

Write-Output (
    'RELEASE_ARTIFACT_RECOVERY_PASS file=' +
    [IO.Path]::GetFileName($destination))
