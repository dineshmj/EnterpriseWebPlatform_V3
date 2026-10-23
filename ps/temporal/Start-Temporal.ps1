<#
.SYNOPSIS
    Starts the local Temporal development server in a console window of its own.

.DESCRIPTION
    Opens "temporal server start-dev" in a classic console window (conhost.exe) - never inside the
    PowerShell ISE or a Windows Terminal tab that runs this script - so Ctrl+C in that window stops
    Temporal cleanly. Then waits until the frontend answers on localhost:7233.

    The Partner Products Journey API starts the Digital Gold purchase workflows there, and the
    Partner Products Worker runs them (task queue partner-products, namespace default).

    By default the workflows are kept in a SQLite file (%LOCALAPPDATA%\EWP\temporal\ewp-dev.db), so
    a purchase waiting for its payment survives a restart of Temporal. -InMemory starts empty and
    forgets everything when the window closes.

    Does nothing when Temporal already answers. Needs the Temporal CLI once:
        winget install Temporal.TemporalCLI

    Stop it with -Stop (its window closes), or Ctrl+C in its window. UI: http://localhost:8233

.EXAMPLE
    .\ps\temporal\Start-Temporal.ps1
    .\ps\temporal\Start-Temporal.ps1 -InMemory
    .\ps\temporal\Start-Temporal.ps1 -Stop
#>
[CmdletBinding()]
param(
    [int] $Port = 7233,
    [int] $UiPort = 8233,
    [string] $DbFile = (Join-Path $env:LOCALAPPDATA 'EWP\temporal\ewp-dev.db'),
    [switch] $InMemory,
    [int] $TimeoutSeconds = 60,
    [switch] $Stop
)

cls;

$ErrorActionPreference = 'Stop'

if ($Stop) {
    # ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
    # │ The process listening on the frontend port is the dev server; its window (cmd /c)
    # │ closes with it. The workflows are in the SQLite file, so a running purchase picks up
    # │ where it was at the next start (a worker may need a moment to reconnect).
    # └──────────────────────────────────────────────────────────────────────────────────────
    $listener = Get-NetTCPConnection -LocalPort $Port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($listener) {
        Stop-Process -Id $listener.OwningProcess -Force
        Write-Host "Stopped Temporal (port $Port); its window closes by itself." -ForegroundColor Yellow
    } else {
        Write-Host 'Temporal was not running.' -ForegroundColor DarkGray
    }
    return
}

$windowTitle = 'Temporal - started by Start-Temporal.ps1, keep open'

function Test-TemporalPort {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('localhost', $Port); return $true } catch { return $false } finally { $client.Dispose() }
}

if (Test-TemporalPort) {
    Write-Host "Temporal is already running on localhost:$Port (UI http://localhost:$UiPort)." -ForegroundColor Green
    return
}

# The CLI: on the PATH, else where winget installs it - its Links folder (a new PATH is seen only
# by new windows) or, when winget created no link, the package folder itself.
$temporal = (Get-Command temporal -ErrorAction SilentlyContinue).Source
if (-not $temporal) {
    $winget = Join-Path $env:LOCALAPPDATA 'Microsoft\WinGet'
    $temporal = @(
        Join-Path $winget 'Links\temporal.exe'
        Get-ChildItem (Join-Path $winget 'Packages') -Directory -Filter 'Temporal.TemporalCLI_*' -ErrorAction SilentlyContinue |
            ForEach-Object { Join-Path $_.FullName 'temporal.exe' }
    ) | Where-Object { Test-Path $_ } | Select-Object -First 1
}
if (-not $temporal) {
    throw "The Temporal CLI was not found. Install it once (winget install Temporal.TemporalCLI), then open a new PowerShell window."
}

# ┌─ SEC ────────────────────────────────────────────────────────────────────────────────
# │ The development server listens on localhost only (--ip 127.0.0.1): it has no TLS and
# │ no authorization, so it must never be reachable from the network (Threat-Model R-16).
# └──────────────────────────────────────────────────────────────────────────────────────
$arguments = "server start-dev --ip 127.0.0.1 --port $Port --ui-port $UiPort --namespace default --log-level warn"
if (-not $InMemory) {
    New-Item -ItemType Directory -Force (Split-Path $DbFile) | Out-Null
    $arguments += " --db-filename `"$DbFile`""
}

# conhost.exe: a classic console window of its own, whichever host runs this script.
Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\conhost.exe') -WorkingDirectory (Split-Path $temporal) `
    -WindowStyle Normal -ArgumentList "cmd.exe /c `"title $windowTitle && `"$temporal`" $arguments`""
Write-Host "Temporal started in its own window (`"$windowTitle`"); waiting for localhost:$Port ..."
Write-Host ($(if ($InMemory) { '  Workflows in memory only (gone when the window closes).' } else { "  Workflows kept in $DbFile" })) -ForegroundColor DarkGray

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    if (Test-TemporalPort) {
        Write-Host "Temporal is running on localhost:$Port; UI http://localhost:$UiPort. Keep its window open; Ctrl+C there stops it." -ForegroundColor Green
        return
    }
}
throw "Temporal did not answer on localhost:$Port within $TimeoutSeconds s. Look at the Temporal window for the reason."