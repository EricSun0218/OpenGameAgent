[CmdletBinding()]
param(
    [Parameter(Mandatory)]
    [string]$SourcePath,

    [Parameter(Mandatory)]
    [string]$CertificatePath,

    [Parameter(Mandatory)]
    [string]$DestinationPath,

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
        $certificate = (
            [Security.Cryptography.X509Certificates.X509Certificate2]::new(
                $der))
    }
    catch {
        throw 'The release artifact recipient certificate is invalid.'
    }
    if ($certificate.HasPrivateKey) {
        $certificate.Dispose()
        throw 'The release artifact recipient must not contain a private key.'
    }
    return $certificate
}

function New-AuthenticatedHeader {
    param(
        [Parameter(Mandatory)]
        [byte[]]$Magic,

        [Parameter(Mandatory)]
        [int]$WrappedKeyLength,

        [Parameter(Mandatory)]
        [long]$PlaintextLength
    )

    $stream = [IO.MemoryStream]::new()
    $writer = [IO.BinaryWriter]::new(
        $stream,
        [Text.UTF8Encoding]::new($false),
        $true)
    try {
        $writer.Write($Magic)
        $writer.Write([byte]1)
        $writer.Write([uint16]$WrappedKeyLength)
        $writer.Write([byte]12)
        $writer.Write([byte]16)
        $writer.Write([int64]$PlaintextLength)
        $writer.Flush()
        return ,$stream.ToArray()
    }
    finally {
        $writer.Dispose()
        $stream.Dispose()
    }
}

if ($MaximumBytes -lt 1 -or $MaximumBytes -gt 134217728L) {
    throw 'The release artifact protection limit is invalid.'
}
if ($null -eq ('Security.Cryptography.AesGcm' -as [type])) {
    throw 'Authenticated artifact protection requires PowerShell 7.'
}

$source = Get-Item -LiteralPath (Resolve-Path -LiteralPath $SourcePath)
if ($source.PSIsContainer -or
    ($source.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or
    $source.Length -gt $MaximumBytes) {
    throw 'The release artifact source is unsafe or exceeds the limit.'
}

$destination = [IO.Path]::GetFullPath($DestinationPath)
if ($source.FullName.Equals(
        $destination,
        [StringComparison]::OrdinalIgnoreCase) -or
    [IO.File]::Exists($destination) -or
    [IO.Directory]::Exists($destination)) {
    throw 'The protected release artifact destination is invalid.'
}
$destinationParent = Get-Item -LiteralPath (
    Split-Path -Parent $destination)
if (-not $destinationParent.PSIsContainer -or
    ($destinationParent.Attributes -band
        [IO.FileAttributes]::ReparsePoint) -ne 0) {
    throw 'The protected release artifact parent is unsafe.'
}

$certificate = Import-PublicCertificate -Path $CertificatePath
$publicKey = $null
$aead = $null
$aesKey = New-Object byte[] 32
$nonce = New-Object byte[] 12
$temporaryPath = (
    $destination + '.tmp.' + [guid]::NewGuid().ToString('N'))
try {
    $publicKey = (
        [Security.Cryptography.X509Certificates.RSACertificateExtensions]::
            GetRSAPublicKey($certificate))
    if ($null -eq $publicKey -or
        $publicKey.ExportParameters($false).Modulus.Length -lt 256) {
        throw 'The release artifact recipient must have a strong RSA key.'
    }

    [Security.Cryptography.RandomNumberGenerator]::Fill($aesKey)
    [Security.Cryptography.RandomNumberGenerator]::Fill($nonce)
    $wrappedKey = $publicKey.Encrypt(
        $aesKey,
        [Security.Cryptography.RSAEncryptionPadding]::OaepSHA256)
    if ($wrappedKey.Length -lt 256 -or $wrappedKey.Length -gt 65535) {
        throw 'The wrapped release artifact key has an invalid length.'
    }

    $magic = [Text.Encoding]::ASCII.GetBytes('GARSEALED1')
    $header = New-AuthenticatedHeader `
        -Magic $magic `
        -WrappedKeyLength $wrappedKey.Length `
        -PlaintextLength $source.Length
    $plaintext = [IO.File]::ReadAllBytes($source.FullName)
    $ciphertext = New-Object byte[] $plaintext.Length
    $tag = New-Object byte[] 16
    $aead = [Security.Cryptography.AesGcm]::new($aesKey, 16)
    $aead.Encrypt($nonce, $plaintext, $ciphertext, $tag, $header)

    $outputLength = (
        [int64]$header.Length +
        $wrappedKey.Length +
        $nonce.Length +
        $tag.Length +
        $ciphertext.Length)
    if ($outputLength -gt ($MaximumBytes + 4096L)) {
        throw 'Protecting the release artifact exceeded the output limit.'
    }
    $output = New-Object byte[] ([int]$outputLength)
    $offset = 0
    [Buffer]::BlockCopy($header, 0, $output, $offset, $header.Length)
    $offset += $header.Length
    [Buffer]::BlockCopy(
        $wrappedKey, 0, $output, $offset, $wrappedKey.Length)
    $offset += $wrappedKey.Length
    [Buffer]::BlockCopy($nonce, 0, $output, $offset, $nonce.Length)
    $offset += $nonce.Length
    [Buffer]::BlockCopy($tag, 0, $output, $offset, $tag.Length)
    $offset += $tag.Length
    [Buffer]::BlockCopy(
        $ciphertext, 0, $output, $offset, $ciphertext.Length)
    [IO.File]::WriteAllBytes($temporaryPath, $output)
    if (-not [IO.File]::Exists($temporaryPath) -or
        ([IO.FileInfo]::new($temporaryPath)).Length -ne $outputLength -or
        [IO.File]::Exists($destination)) {
        throw 'Protecting the release artifact did not produce a safe output.'
    }
    [IO.File]::Move($temporaryPath, $destination)
}
finally {
    [Security.Cryptography.CryptographicOperations]::ZeroMemory($aesKey)
    if ($null -ne $aead) {
        $aead.Dispose()
    }
    if ($null -ne $publicKey) {
        $publicKey.Dispose()
    }
    $certificate.Dispose()
    if ([IO.File]::Exists($temporaryPath)) {
        [IO.File]::Delete($temporaryPath)
    }
}

Write-Output (
    'RELEASE_ARTIFACT_PROTECTION_PASS file=' +
    [IO.Path]::GetFileName($destination))
