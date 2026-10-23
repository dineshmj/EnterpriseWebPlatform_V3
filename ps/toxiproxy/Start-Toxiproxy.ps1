<#
.SYNOPSIS
    Starts the local fault proxy (Toxiproxy) in a console window of its own.

.DESCRIPTION
    In Development every server-to-server call to an EWP API goes through this proxy: a BFF to its
    Journey or Domain API, a Journey API to a Domain API, a Kafka subscriber to its API, the
    Partner Products Worker to the Partner Products API. Normally every route is open and the
    proxy only passes the bytes on (TLS stays end to end: the proxy never sees the content).
    daniel.ops can cut a route, or slow it down, on Operations > Simulator & Fault Controls to see
    how the callers cope when that API is down.

    The routes are in ps\toxiproxy\toxiproxy.json (one file, in the repository). A restart opens
    every route again; so does Reset.ps1 (it stops the proxy with the rest of the platform).

    NOT through the proxy: the browser to a BFF, the IDP and the Partner Bank's authorization
    server (token issuer and discovery), Kafka, and the Partner Bank API (its TLS name points at
    the Partner Bank's machine). Production, load and stress environments use direct addresses
    and have no proxy at all.

    Does nothing when the proxy already answers. Needs Toxiproxy once: -Install downloads the
    pinned release (v2.12.0) from GitHub into %LOCALAPPDATA%\EWP\toxiproxy and keeps it only when
    its SHA-256 matches the release's checksums.txt.

    Like Kafka and Temporal it is platform plumbing, started on its own - before the .NET solution
    and the Node.js services, whose calls go through it - and left running while they restart.
    Stop it with -Stop (its window closes), or Ctrl+C in its window.

.EXAMPLE
    .\ps\toxiproxy\Start-Toxiproxy.ps1 -Install     # once: download, check, then start
    .\ps\toxiproxy\Start-Toxiproxy.ps1
    .\ps\toxiproxy\Start-Toxiproxy.ps1 -Stop        # stop it (every route opens again at the next start)
#>
[CmdletBinding()]
param(
    [int] $AdminPort = 8474,
    [string] $Executable = (Join-Path $env:LOCALAPPDATA 'EWP\toxiproxy\toxiproxy-server.exe'),
    [int] $TimeoutSeconds = 30,
    [switch] $Install,
    [switch] $Stop
)

$ErrorActionPreference = 'Stop'

cls;

if ($Stop) {
    # The process listening on the admin port is the proxy; its window (cmd /c) closes with it.
    $listener = Get-NetTCPConnection -LocalPort $AdminPort -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        Stop-Process -Id $listener.OwningProcess -Force
        Write-Host "Stopped the fault proxy (admin port $AdminPort); its window closes by itself." -ForegroundColor Yellow
    } else {
        Write-Host "The fault proxy was not running." -ForegroundColor DarkGray
    }
    return
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
# │ A downloaded executable runs on this machine: the version is pinned (never "latest")
# │ and the file is kept only when its SHA-256 matches the release's checksums.txt -
# │ a damaged or swapped download is deleted, never started.
# └──────────────────────────────────────────────────────────────────────────────────────
$version = 'v2.12.0'
$asset = 'toxiproxy-server-windows-amd64.exe'

if ($Install -and -not (Test-Path $Executable)) {
    $dir = Split-Path $Executable
    New-Item -ItemType Directory -Force $dir | Out-Null
    $base = "https://github.com/Shopify/toxiproxy/releases/download/$version"
    $download = Join-Path $dir "$asset.download"
    Write-Host "Downloading Toxiproxy $version ..."
    Invoke-WebRequest "$base/$asset" -OutFile $download -UseBasicParsing
    $checksums = (Invoke-WebRequest "$base/checksums.txt" -UseBasicParsing).Content
    if ($checksums -is [byte[]]) { $checksums = [Text.Encoding]::UTF8.GetString($checksums) }
    $line = ($checksums -split "`r?`n") | Where-Object { $_ -match "\s\*?$([regex]::Escape($asset))$" } | Select-Object -First 1
    if (-not $line) { Remove-Item $download; throw "checksums.txt of Toxiproxy $version has no line for $asset; nothing installed." }
    $expected = ($line -split '\s+')[0]
    $actual = (Get-FileHash $download -Algorithm SHA256).Hash
    if ($actual -ne $expected.ToUpperInvariant()) { Remove-Item $download; throw "The download's SHA-256 ($actual) is not the release's ($expected); deleted, nothing installed." }
    # Installed meanwhile (another window): keep that one, drop this download.
    if (Test-Path $Executable) { Remove-Item $download } else { Move-Item $download $Executable }
    Write-Host "Installed Toxiproxy $version in $dir (SHA-256 checked)." -ForegroundColor Green
}

$windowTitle = 'Fault proxy (Toxiproxy) - started by Start-Toxiproxy.ps1, keep open'
$config = Join-Path $PSScriptRoot 'toxiproxy.json'

function Test-AdminPort {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('127.0.0.1', $AdminPort); return $true } catch { return $false } finally { $client.Dispose() }
}

if (Test-AdminPort) {
    Write-Host "The fault proxy is already running (admin API 127.0.0.1:$AdminPort)." -ForegroundColor Green
    return
}

# On the PATH, else in the folder -Install uses.
$toxiproxy = (Get-Command toxiproxy-server -ErrorAction SilentlyContinue).Source
if (-not $toxiproxy -and (Test-Path $Executable)) { $toxiproxy = $Executable }
if (-not $toxiproxy) {
    throw "Toxiproxy is not installed ($Executable). Install it once with: .\ps\toxiproxy\Start-Toxiproxy.ps1 -Install"
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
# │ The admin API has no authentication: whoever reaches it can cut any route. It
# │ listens on 127.0.0.1 only (-host), and so does every route (listen 127.0.0.1:471xx
# │ in toxiproxy.json); only the Operations Journey API, on this machine, calls it.
# │ The proxy passes TLS through untouched - it never holds a key or sees a token.
# └──────────────────────────────────────────────────────────────────────────────────────
$arguments = "-host 127.0.0.1 -port $AdminPort -config `"$config`""

# conhost.exe: a classic console window of its own, whichever host runs this script.
Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\conhost.exe') -WorkingDirectory (Split-Path $toxiproxy) `
    -WindowStyle Normal -ArgumentList "cmd.exe /c `"title $windowTitle && `"$toxiproxy`" $arguments`""
Write-Host "The fault proxy started in its own window (`"$windowTitle`"); waiting for 127.0.0.1:$AdminPort ..."

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 1
    if (Test-AdminPort) {
        Write-Host "The fault proxy is running; every route open ($config)." -ForegroundColor Green
        return
    }
}
throw "The fault proxy did not answer on 127.0.0.1:$AdminPort within $TimeoutSeconds s. Look at its window for the reason."