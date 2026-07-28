[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

$protector = Join-Path $PSScriptRoot 'Protect-ReleaseArtifact.ps1'
$recoverer = Join-Path $PSScriptRoot 'Unprotect-ReleaseArtifact.ps1'
$temporaryRoot = Join-Path (
    [IO.Path]::GetTempPath()
) ('game-agent-artifact-protection-' + [guid]::NewGuid().ToString('N'))
$originalKey = [Environment]::GetEnvironmentVariable(
    'GAME_AGENT_ARTIFACT_DECRYPTION_PFX')

try {
    $null = New-Item -ItemType Directory -Path $temporaryRoot
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $request = (
        [Security.Cryptography.X509Certificates.CertificateRequest]::new(
            'CN=Game Agent Artifact Protection Self Test',
            $rsa,
            [Security.Cryptography.HashAlgorithmName]::SHA256,
            [Security.Cryptography.RSASignaturePadding]::Pkcs1))
    $usage = (
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::
            DataEncipherment -bor
        [Security.Cryptography.X509Certificates.X509KeyUsageFlags]::
            KeyEncipherment)
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509KeyUsageExtension]::new(
            $usage,
            $true))
    $oids = [Security.Cryptography.OidCollection]::new()
    $null = $oids.Add(
        [Security.Cryptography.Oid]::new(
            '1.3.6.1.4.1.311.80.1',
            'Document Encryption'))
    $request.CertificateExtensions.Add(
        [Security.Cryptography.X509Certificates.X509EnhancedKeyUsageExtension]::
            new($oids, $true))
    $certificate = $request.CreateSelfSigned(
        [DateTimeOffset]::UtcNow.AddDays(-1),
        [DateTimeOffset]::UtcNow.AddDays(1))

    $der = $certificate.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Cert)
    $pemBody = [Convert]::ToBase64String(
        $der,
        [Base64FormattingOptions]::InsertLineBreaks)
    $pem = (
        "-----BEGIN CERTIFICATE-----`n" +
        ($pemBody -replace "`r`n", "`n") +
        "`n-----END CERTIFICATE-----`n")
    $certificatePath = Join-Path $temporaryRoot 'recipient.pem'
    [IO.File]::WriteAllText(
        $certificatePath,
        $pem,
        [Text.UTF8Encoding]::new($false))

    $sourcePath = Join-Path $temporaryRoot 'source.bin'
    $sourceBytes = New-Object byte[] 8193
    for ($index = 0; $index -lt $sourceBytes.Length; $index++) {
        $sourceBytes[$index] = [byte]($index % 251)
    }
    [IO.File]::WriteAllBytes($sourcePath, $sourceBytes)

    $protectedPath = Join-Path $temporaryRoot 'source.bin.sealed'
    & $protector `
        -SourcePath $sourcePath `
        -CertificatePath $certificatePath `
        -DestinationPath $protectedPath

    $pfx = $certificate.Export(
        [Security.Cryptography.X509Certificates.X509ContentType]::Pkcs12,
        '')
    [Environment]::SetEnvironmentVariable(
        'GAME_AGENT_ARTIFACT_DECRYPTION_PFX',
        [Convert]::ToBase64String($pfx))
    $recoveredPath = Join-Path $temporaryRoot 'recovered.bin'
    & $recoverer `
        -SourcePath $protectedPath `
        -CertificatePath $certificatePath `
        -DestinationPath $recoveredPath `
        -RequireInjectedKey

    $sourceHash = (
        Get-FileHash -LiteralPath $sourcePath -Algorithm SHA256).Hash
    $recoveredHash = (
        Get-FileHash -LiteralPath $recoveredPath -Algorithm SHA256).Hash
    if (-not $sourceHash.Equals(
            $recoveredHash,
            [StringComparison]::OrdinalIgnoreCase)) {
        throw 'The artifact protection round trip changed the payload.'
    }

    $tamperedPath = Join-Path $temporaryRoot 'tampered.sealed'
    $tampered = [IO.File]::ReadAllBytes($protectedPath)
    $offset = [Math]::Floor($tampered.Length / 2)
    $tampered[$offset] = $tampered[$offset] -bxor 0x01
    [IO.File]::WriteAllBytes($tamperedPath, $tampered)
    $rejected = $false
    try {
        & $recoverer `
            -SourcePath $tamperedPath `
            -CertificatePath $certificatePath `
            -DestinationPath (Join-Path $temporaryRoot 'tampered.bin') `
            -RequireInjectedKey
    }
    catch {
        $rejected = $true
    }
    if (-not $rejected) {
        throw 'Tampered protected content was accepted.'
    }

    Write-Output 'RELEASE_ARTIFACT_PROTECTION_SELF_TEST_PASS'
}
finally {
    [Environment]::SetEnvironmentVariable(
        'GAME_AGENT_ARTIFACT_DECRYPTION_PFX',
        $originalKey)
    if ($null -ne (Get-Variable certificate -ErrorAction SilentlyContinue)) {
        $certificate.Dispose()
    }
    if ($null -ne (Get-Variable rsa -ErrorAction SilentlyContinue)) {
        $rsa.Dispose()
    }
    if ([IO.Directory]::Exists($temporaryRoot)) {
        Remove-Item -LiteralPath $temporaryRoot -Recurse -Force
    }
}
