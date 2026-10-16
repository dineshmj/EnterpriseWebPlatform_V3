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
    Export-PfxCertificate -Cert $Certificate -FilePath (Join-Path $OutputFolder "$Name.pfx") -Password $secure | Out-Null
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

try {
    # ------------------------------------------------------------------------------- server and client

    $issued = @(
        @{ Name = 'partner-bank-server'; Subject = $serverSubject; Purpose = '1.3.6.1.5.5.7.3.1'; Usage = @('DigitalSignature', 'KeyEncipherment');
           Extra = @('2.5.29.17={text}' + (($partnerHostNames | ForEach-Object { "DNS=$_" }) -join '&')) },
        @{ Name = 'ewp-partner-client'; Subject = $clientSubject; Purpose = '1.3.6.1.5.5.7.3.2'; Usage = @('DigitalSignature'); Extra = @() }
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
        $created = New-SelfSignedCertificate -Type Custom -Subject $certificate.Subject -Signer $root `
            -KeyAlgorithm RSA -KeyLength 3072 -HashAlgorithm SHA256 -KeyExportPolicy Exportable `
            -KeyUsage $certificate.Usage `
            -TextExtension (@("2.5.29.37={text}$($certificate.Purpose)") + $certificate.Extra) `
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

Write-Host ''
Write-Host "Written to $OutputFolder"
Write-Host '  Home PC (Partner Bank): copy ewp-trust-root.cer, partner-bank-server.pfx and partner-bank-server.pfx.password'
Write-Host '  Never copy ewp-trust-root.pfx or ewp-partner-client.pfx off this laptop.'