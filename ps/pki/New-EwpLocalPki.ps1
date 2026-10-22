<#
.SYNOPSIS
    Creates the PoC's local certificate authority and the certificates for mutual TLS between EWP
    (our bank) and the Partner Bank (the EnterpriseWebPlatform_V3_PartnerBank repository).

.DESCRIPTION
    DEVELOPMENT ONLY. Plays the part of a neutral trust body that both organisations accept (in real
    life: a commercial CA, or a scheme registry such as Australia's Consumer Data Right register).

    Writes to %USERPROFILE%\.ewp-pki (outside both repositories - nothing here is ever committed):

        ewp-trust-root.cer               the root certificate (public): both organisations trust it
        ewp-trust-root.pfx  + .password  the root's private key: stays on THIS laptop, signs the others

        partner-bank-server.pfx + .password   the Partner Bank's TLS server certificate, for
                                              partner-auth / partner-products / partner-api.ewp.home.arpa
                                              -> copy to the home PC with ewp-trust-root.cer

        ewp-partner-client.pfx  + .password   EWP's client certificate (mutual TLS towards the
                                              Partner Bank) - stays on this laptop, never on the PC

        ewp-request-signing.pfx + .password   EWP's request-signing key (ECDSA P-256, ES256): EWP signs
                                              its pushed authorization requests with it (JAR) - the key
                                              stays on this laptop; the Partner Bank gets ONLY
                                              ewp-request-signing.cer (the public part) -> copy to the PC

        kafka-broker.pfx        + .password   the local Kafka broker's TLS server certificate (localhost,
                                              127.0.0.1): SASL_SSL on localhost:9092 - stays on this laptop

        ewp-trust-root.pem                    the root certificate in PEM form, for the clients that read
                                              PEM (Kafka: librdkafka, the Kafka CLI tools and Kafka UI)

        self-service-web-client.key.pem + .password       the client keys of the customers' channel
        self-service-journey-api-client.key.pem + .password  (My EWP, FAPI 2.0 private_key_jwt): EC
        self-service-*-client.public.jwk                  P-256 key pairs, NOT certificates - the
                                              private key as encrypted PKCS#8 PEM (the Node services read
                                              PEM), the public key as a JWK that the IDP registers. Made by
                                              New-EwpClientKeys.mjs (needs Node.js) - stay on this laptop

    Windows' certificate stores are used only briefly to create and sign the certificates, and are
    cleaned again: the services trust ewp-trust-root.cer through their own configuration. Browsers
    that open Partner Bank pages need the root in "Trusted Root Certification Authorities" - that is a
    separate, deliberate step (see the Partner Bank README).

.PARAMETER Force
    Re-issue the server and client certificates even if they exist (the root is kept; delete the
    folder to start a new trust root - every certificate must then be issued again).

.EXAMPLE
    powershell.exe -ExecutionPolicy Bypass -File .\ps\pki\New-EwpLocalPki.ps1
#>
[CmdletBinding()]
param(
    [string]$OutputFolder = (Join-Path $env:USERPROFILE '.ewp-pki'),
    [switch]$Force
)

$ErrorActionPreference = 'Stop'

$partnerHostNames = @('partner-auth.ewp.home.arpa', 'partner-products.ewp.home.arpa', 'partner-api.ewp.home.arpa')
$rootSubject      = 'CN=EWP Local Trust Root (DEVELOPMENT ONLY), O=EWP V3 PoC'
$serverSubject    = 'CN=partner-auth.ewp.home.arpa, O=Partner Bank (PoC)'
$clientSubject    = 'CN=EWP BSS Partner Products Client, O=Four Walls Inc.'
$signingSubject   = 'CN=EWP BSS Request Signing, O=Four Walls Inc.'
$kafkaSubject     = 'CN=localhost, O=EWP V3 PoC (Kafka broker)'
$store            = 'Cert:\CurrentUser\My'

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
# │ A random password per private-key file (32 bytes from the OS's cryptographic random
# │ generator), kept next to it - outside the repositories. Development only: in production
# │ the keys live in a secret store or an HSM and never in files.
# └──────────────────────────────────────────────────────────────────────────────────────
function New-RandomPassword {
    $bytes = New-Object byte[] 32
    $rng = [System.Security.Cryptography.RandomNumberGenerator]::Create()
    try { $rng.GetBytes($bytes) } finally { $rng.Dispose() }
    return [Convert]::ToBase64String($bytes)
}

function Export-WithPassword {
    param($Certificate, [string]$Name)

    $password = New-RandomPassword
    $secure = ConvertTo-SecureString -String $password -AsPlainText -Force
    # AES-256 protection of the .pfx (Windows' default is 3DES): the Kafka broker's Java reads it too.
    Export-PfxCertificate -Cert $Certificate -FilePath (Join-Path $OutputFolder "$Name.pfx") -Password $secure -CryptoAlgorithmOption AES256_SHA256 | Out-Null
    Export-Certificate -Cert $Certificate -FilePath (Join-Path $OutputFolder "$Name.cer") -Type CERT | Out-Null
    [System.IO.File]::WriteAllText((Join-Path $OutputFolder "$Name.pfx.password"), $password)
}

function Remove-FromStores {
    param([string]$Thumbprint)

    foreach ($location in 'Cert:\CurrentUser\My', 'Cert:\CurrentUser\CA') {
        $path = Join-Path $location $Thumbprint
        if (Test-Path $path) { Remove-Item $path -DeleteKey -ErrorAction SilentlyContinue; Remove-Item $path -ErrorAction SilentlyContinue }
    }
}

New-Item -ItemType Directory -Force -Path $OutputFolder | Out-Null

# --------------------------------------------------------------------------------------------- root

$rootPfx = Join-Path $OutputFolder 'ewp-trust-root.pfx'
if (Test-Path $rootPfx) {
    $rootPassword = ConvertTo-SecureString -String ([System.IO.File]::ReadAllText("$rootPfx.password")) -AsPlainText -Force
    $root = Import-PfxCertificate -FilePath $rootPfx -CertStoreLocation $store -Password $rootPassword -Exportable
    Write-Host "Using the existing trust root ($($root.Thumbprint))."
}
else {
    # ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    # │ A CA that can sign end-entity certificates only (path length 0): it cannot create
    # │ further CAs. RSA 3072 / SHA-256, five years.
    # └──────────────────────────────────────────────────────────────────────────────────────
    $root = New-SelfSignedCertificate -Type Custom -Subject $rootSubject `
        -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
        -KeyUsage CertSign, CRLSign, DigitalSignature `
        -TextExtension @('2.5.29.19={critical}{text}ca=1&pathlength=0') `
        -NotAfter (Get-Date).AddYears(5) -CertStoreLocation $store
    Export-WithPassword -Certificate $root -Name 'ewp-trust-root'
    Write-Host "Created the trust root ($($root.Thumbprint))."
}

# PEM copy of the root (public): Kafka's clients and broker read their trust anchor as PEM.
$rootPem = "-----BEGIN CERTIFICATE-----`n" +
    (([Convert]::ToBase64String($root.RawData) -split '(.{64})' | Where-Object { $_ }) -join "`n") +
    "`n-----END CERTIFICATE-----`n"
[System.IO.File]::WriteAllText((Join-Path $OutputFolder 'ewp-trust-root.pem'), $rootPem)

try {
    # ---------------------------------------------------------------------- server, client and signing

    # ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
    # │ EWP's request-signing key is SEPARATE from its mutual-TLS key (one key, one job): a
    # │ leaked TLS key cannot sign requests, and the signing key can be rotated on its own.
    # │ ECDSA P-256 (JWS alg ES256 - allowed by FAPI 2.0); no Extended Key Usage, so it can
    # │ neither serve TLS nor authenticate a TLS client.
    # └──────────────────────────────────────────────────────────────────────────────────────
    $issued = @(
        @{ Name = 'partner-bank-server'; Subject = $serverSubject; Algorithm = 'RSA'; Purpose = '1.3.6.1.5.5.7.3.1'; Usage = @('DigitalSignature', 'KeyEncipherment');
           Extra = @('2.5.29.17={text}' + (($partnerHostNames | ForEach-Object { "DNS=$_" }) -join '&')) },
        @{ Name = 'ewp-partner-client'; Subject = $clientSubject; Algorithm = 'RSA'; Purpose = '1.3.6.1.5.5.7.3.2'; Usage = @('DigitalSignature'); Extra = @() },
        @{ Name = 'ewp-request-signing'; Subject = $signingSubject; Algorithm = 'ECDSA_nistP256'; Purpose = $null; Usage = @('DigitalSignature'); Extra = @() },
        # ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        # │ The Kafka broker's TLS certificate names localhost and 127.0.0.1 only, so the clients'
        # │ host-name check (ssl.endpoint.identification.algorithm=https) passes for the local
        # │ broker and for nothing else. Server authentication only: the clients prove who they
        # │ are with SCRAM inside the TLS tunnel, not with certificates.
        # └──────────────────────────────────────────────────────────────────────────────────────
        @{ Name = 'kafka-broker'; Subject = $kafkaSubject; Algorithm = 'RSA'; Purpose = '1.3.6.1.5.5.7.3.1'; Usage = @('DigitalSignature', 'KeyEncipherment');
           Extra = @('2.5.29.17={text}DNS=localhost&IPAddress=127.0.0.1') }
    )

    foreach ($certificate in $issued) {
        $pfx = Join-Path $OutputFolder "$($certificate.Name).pfx"
        if ((Test-Path $pfx) -and -not $Force) {
            Write-Host "$($certificate.Name): exists - kept (use -Force to issue it again)."
            continue
        }

        # ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
        # │ Each certificate is limited to ONE purpose (Extended Key Usage): the server certificate
        # │ cannot authenticate a client, and EWP's client certificate cannot serve TLS. One year,
        # │ never beyond the root's own validity.
        # └──────────────────────────────────────────────────────────────────────────────────────
        $extensions = @($certificate.Extra)
        if ($certificate.Purpose) { $extensions = @("2.5.29.37={text}$($certificate.Purpose)") + $extensions }
        $options = if ($certificate.Algorithm -eq 'RSA') { @{ KeyAlgorithm = 'RSA'; KeyLength = 3072 } } else { @{ KeyAlgorithm = $certificate.Algorithm } }
        if ($extensions.Count -gt 0) { $options.TextExtension = $extensions }
        $created = New-SelfSignedCertificate -Type Custom -Subject $certificate.Subject -Signer $root @options `
            -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
            -KeyUsage $certificate.Usage `
            -NotAfter (Get-Date).AddYears(1) -CertStoreLocation $store
        try {
            Export-WithPassword -Certificate $created -Name $certificate.Name
            Write-Host "$($certificate.Name): issued ($($created.Thumbprint)), $($created.Subject)."
        }
        finally {
            Remove-FromStores -Thumbprint $created.Thumbprint
        }
    }
}
finally {
    Remove-FromStores -Thumbprint $root.Thumbprint
}

# ---------------------------------------------------------------------- client keys (private_key_jwt)

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
# │ The customers' channel authenticates to the IDP with private_key_jwt (FAPI 2.0): plain
# │ key pairs, no certificate - the IDP trusts the registered PUBLIC key, not a CA. Node.js
# │ writes them (PowerShell 5.1 cannot export PKCS#8 PEM); a new key needs an IDP restart.
# └──────────────────────────────────────────────────────────────────────────────────────
$node = Get-Command node -ErrorAction SilentlyContinue
if ($node) {
    Write-Host 'Client keys (private_key_jwt):'
    $keyArgs = @((Join-Path $PSScriptRoot 'New-EwpClientKeys.mjs'), $OutputFolder)
    if ($Force) { $keyArgs += '--force' }
    & $node.Source @keyArgs
    if ($LASTEXITCODE -ne 0) { throw 'New-EwpClientKeys.mjs failed.' }
}
else {
    Write-Warning 'Node.js is not on PATH: the client keys of the customers channel (My EWP) were NOT created.'
}

Write-Host ''
Write-Host "Written to $OutputFolder"
Write-Host '  Home PC (Partner Bank): copy ewp-trust-root.cer, ewp-request-signing.cer, partner-bank-server.pfx and partner-bank-server.pfx.password'
Write-Host '  Never copy ewp-trust-root.pfx, ewp-partner-client.pfx, ewp-request-signing.pfx, kafka-broker.pfx or the *.key.pem files off this laptop.'
Write-Host '  Kafka (new kafka-broker certificate): stop Kafka, run .\ps\kafka\Setup-KafkaSecurity.ps1 -Phase Secure, start Kafka.'