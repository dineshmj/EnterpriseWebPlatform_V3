<#
.SYNOPSIS
    Switches two-step sign-in (MFA) on or off for the whole local platform, in every place that must agree.

.DESCRIPTION
    MFA is one switch in eight places (ReadMe.txt, section f); they must always be equal, or the IDP
    would sign people in without a code while the APIs ask for a recent one (or the other way round):

      src\IDP\appsettings.json                                  Mfa:Enabled - the sign-in itself
      src\Microservices\Payments\API\appsettings.json           Mfa:Enabled - step-up on decisions
      src\Microservices\Accounts\API\appsettings.json           Mfa:Enabled
      src\Microservices\Compliance\API\appsettings.json         Mfa:Enabled
      src\Microservices\CustomerKyc\API\appsettings.json        Mfa:Enabled
      src\Microservices\PartnerProducts\API\appsettings.json    Mfa:Enabled - step-up on the Outbox resend
      src\Microservices\Operations\JourneyApi\runnow.bat        OPERATIONS_MFA_ENABLED (the simulator switch)
      tst\playwright\.env                                       EWP_MFA_ENABLED (the automated MFA test)

    Only the switch's value is changed - comments, encodings and line endings stay as they are; the
    .env file's other values are neither read out nor shown. Restart the IDP, those APIs and the Node
    services afterwards (Visual Studio; .\ps\run\Start-NodeServices.ps1 -Stop, then without -Stop).

    Development only: outside Development MFA cannot be switched off (the IDP and the APIs refuse to start).

.EXAMPLE
    .\ps\Toggle-MFA.ps1 -On
.EXAMPLE
    .\ps\Toggle-MFA.ps1 -Off
#>
param(
    [switch] $On,
    [switch] $Off
)

$ErrorActionPreference = 'Stop'
if ($On -eq $Off) { throw 'Pass exactly one of -On or -Off.' }
$value = if ($On) { 'true' } else { 'false' }
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path   # the repository root (this script is in ps\)

# Rewrites one file with a regex, keeping its encoding (BOM or not) and its line endings.
function Set-InFile([string] $relative, [string] $pattern, [string] $replacement) {
    $path = Join-Path $root $relative
    if (-not (Test-Path $path)) { Write-Host "  missing  $relative" -ForegroundColor Red; return $false }
    $bytes = [IO.File]::ReadAllBytes($path)
    $hasBom = $bytes.Length -ge 3 -and $bytes[0] -eq 0xEF -and $bytes[1] -eq 0xBB -and $bytes[2] -eq 0xBF
    $text = [IO.File]::ReadAllText($path)
    if (-not [regex]::IsMatch($text, $pattern)) { Write-Host "  NOT FOUND  $relative" -ForegroundColor Red; return $false }
    [IO.File]::WriteAllText($path, [regex]::Replace($text, $pattern, $replacement), (New-Object Text.UTF8Encoding($hasBom)))
    Write-Host "  $value  $relative" -ForegroundColor Green
    return $true
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
# │ All eight places, or say which one failed: a half-switched platform (sign-in without
# │ a code, but APIs demanding a recent one - or the reverse) is the misconfiguration this
# │ script exists to prevent. Only the switch's own value is touched.
# └──────────────────────────────────────────────────────────────────────────────────────
Write-Host "MFA -> $value" -ForegroundColor Cyan
$json = '("Mfa"\s*:\s*\{\s*"Enabled"\s*:\s*)(true|false)'
$results = @(
    Set-InFile 'src\IDP\appsettings.json' $json "`${1}$value"
    Set-InFile 'src\Microservices\Payments\API\appsettings.json' $json "`${1}$value"
    Set-InFile 'src\Microservices\Accounts\API\appsettings.json' $json "`${1}$value"
    Set-InFile 'src\Microservices\Compliance\API\appsettings.json' $json "`${1}$value"
    Set-InFile 'src\Microservices\CustomerKyc\API\appsettings.json' $json "`${1}$value"
    Set-InFile 'src\Microservices\PartnerProducts\API\appsettings.json' $json "`${1}$value"
    Set-InFile 'src\Microservices\Operations\JourneyApi\runnow.bat' '(set "OPERATIONS_MFA_ENABLED=)(true|false)' "`${1}$value"
    Set-InFile 'tst\playwright\.env' '(?m)^(EWP_MFA_ENABLED=)(true|false)' "`${1}$value"
)

if ($results -contains $false) {
    Write-Host "`nNot every place was switched - fix the ones marked above before starting the platform." -ForegroundColor Red
    exit 1
}
Write-Host "`nRestart the IDP and the APIs (Visual Studio) and the Node services (.\ps\run\Start-NodeServices.ps1 -Stop, then again)." -ForegroundColor Cyan