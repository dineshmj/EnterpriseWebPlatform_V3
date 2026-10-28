<#
.SYNOPSIS
    Installs WireMock standalone (one jar) for the Node.js services' tests.

.DESCRIPTION
    The Journey APIs' tests (Partner Products, Audit, Operations: Jest, "pnpm test") play their
    neighbours - the IDP's token endpoint, the domain APIs, the simulators, the fault proxy - with
    WireMock standalone, the Java server the customer's NestJS teams use. The tests start it themselves (127.0.0.1, a free port, at most 128 MB) and
    stop it when they end: nothing keeps running.

    This script puts the pinned release (3.13.2) from Maven Central into
    %LOCALAPPDATA%\EWP\wiremock and keeps it only when its SHA-256 matches the one Maven Central
    publishes for it. Needs Java (the one Kafka uses is enough). Does nothing when it is there.

.EXAMPLE
    .\ps\wiremock\Install-WireMock.ps1
#>
[CmdletBinding()]
param(
    [string] $Folder = (Join-Path $env:LOCALAPPDATA 'EWP\wiremock')
)

cls;

$ErrorActionPreference = 'Stop'

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
# │ A downloaded jar runs on this machine: the version is pinned (never "latest", never a
# │ beta) and the file is kept only when its SHA-256 matches Maven Central's .sha256 -
# │ a damaged or swapped download is deleted, never run.
# └──────────────────────────────────────────────────────────────────────────────────────
$version = '3.13.2'
$jar = Join-Path $Folder "wiremock-standalone-$version.jar"

if (Test-Path $jar) {
    Write-Host "WireMock $version is already installed: $jar" -ForegroundColor Green
    return
}
if (-not (Get-Command java -ErrorAction SilentlyContinue)) {
    throw 'Java was not found on the PATH. WireMock standalone needs it (the Java Kafka uses is enough).'
}

New-Item -ItemType Directory -Force $Folder | Out-Null
$base = "https://repo1.maven.org/maven2/org/wiremock/wiremock-standalone/$version/wiremock-standalone-$version.jar"
$download = "$jar.download"
Write-Host "Downloading WireMock standalone $version ..."
Invoke-WebRequest $base -OutFile $download -UseBasicParsing
$published = (Invoke-WebRequest "$base.sha256" -UseBasicParsing).Content
if ($published -is [byte[]]) { $published = [Text.Encoding]::ASCII.GetString($published) }
$expected = ($published.Trim() -split '\s+')[0]
$actual = (Get-FileHash $download -Algorithm SHA256).Hash
if ($actual -ne $expected.ToUpperInvariant()) {
    Remove-Item $download
    throw "The download's SHA-256 ($actual) is not Maven Central's ($expected); deleted, nothing installed."
}
Move-Item $download $jar
Write-Host "Installed WireMock standalone $version in $Folder (SHA-256 checked)." -ForegroundColor Green