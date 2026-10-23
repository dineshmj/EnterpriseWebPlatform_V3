<#
.SYNOPSIS
    Starts (or stops, or restarts) everything outside Visual Studio, in order:
    (1) Kafka, (2) Temporal, (3) the fault proxy (Toxiproxy), (4) the eight Node.js services.

.DESCRIPTION
    One command for what ReadMe.txt otherwise lists step by step. Each part keeps its own script
    and window; this one only puts them in order:

        1. Kafka            ps\kafka\Start-Kafka.ps1            localhost:9092
        2. Temporal         ps\temporal\Start-Temporal.ps1      localhost:7233 (UI http://localhost:8233)
        3. Fault proxy      ps\toxiproxy\Start-Toxiproxy.ps1    admin 127.0.0.1:8474
           -- waits here for the .NET solution (the IDP answering on its port): start it in Visual
              Studio (Ctrl+F5) when asked. Kafka and the fault proxy are already up, so the .NET
              services find them at once.
        4. Node.js services ps\run\Start-NodeServices.ps1       (they need the IDP)

    A part that is already running is left as it is. Run it before or after starting Visual
    Studio; it waits only when the IDP is not up yet.

    -Stop stops all four in reverse order (Node.js services, fault proxy, Temporal, Kafka) and
    their windows close; the .NET solution keeps running (stop it in Visual Studio). Kafka is
    stopped with Stop-Kafka.ps1 (a hard stop it recovers from).
    -Restart does -Stop, then starts all four again.

.EXAMPLE
    .\ps\run\Start-Non-DotNet-Services.ps1              # start what is not running yet
    .\ps\run\Start-Non-DotNet-Services.ps1 -Restart     # stop all four, start them again
    .\ps\run\Start-Non-DotNet-Services.ps1 -Stop        # stop all four
#>
param(
    [switch] $Stop,
    [switch] $Restart,
    # How long to wait for the .NET solution's IDP before giving up on step 4.
    [int] $WaitForIdpMinutes = 15
)

$ErrorActionPreference = 'Stop'

if ($Stop -and $Restart) {
    Write-Host 'Use -Stop or -Restart, not both.' -ForegroundColor Red
    exit 1
}

Clear-Host
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path   # the repository root (this script is in ps\<area>)

# ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
# │ Each part's script starts with "cls"; inside this one that would wipe the earlier
# │ parts' output. A function named Clear-Host wins over the cmdlet (and its alias cls)
# │ for the scripts called from here, so their screens are kept.
# └──────────────────────────────────────────────────────────────────────────────────────
function Clear-Host { }

$idpPort = 46392   # src\IDP launchSettings: https://idp.dev.localhost:46392

function Step([string] $text) {
    Write-Host ''
    Write-Host "=== $text ===" -ForegroundColor Cyan
}

function Stop-All {
    Step 'Stopping the Node.js services'
    & (Join-Path $root 'ps\run\Start-NodeServices.ps1') -Stop
    Step 'Stopping the fault proxy'
    & (Join-Path $root 'ps\toxiproxy\Start-Toxiproxy.ps1') -Stop
    Step 'Stopping Temporal'
    & (Join-Path $root 'ps\temporal\Start-Temporal.ps1') -Stop
    Step 'Stopping Kafka'
    & (Join-Path $root 'ps\kafka\Stop-Kafka.ps1')
}

if ($Stop -or $Restart) {
    Stop-All
    if ($Stop) {
        Write-Host ''
        Write-Host 'All four stopped. The .NET solution is still running - stop it in Visual Studio.' -ForegroundColor Yellow
        return
    }
}

Step '1/4 Kafka'
& (Join-Path $root 'ps\kafka\Start-Kafka.ps1')

Step '2/4 Temporal'
& (Join-Path $root 'ps\temporal\Start-Temporal.ps1')

Step '3/4 Fault proxy'
& (Join-Path $root 'ps\toxiproxy\Start-Toxiproxy.ps1')

Step '4/4 Node.js services'
if (-not (Get-NetTCPConnection -LocalPort $idpPort -State Listen -ErrorAction SilentlyContinue)) {
    Write-Host "Waiting for the .NET solution (the IDP on port $idpPort): start it in Visual Studio now (Ctrl+F5)." -ForegroundColor Yellow
    Write-Host "Waiting up to $WaitForIdpMinutes minutes; Ctrl+C stops waiting (then run .\ps\run\Start-NodeServices.ps1 later)." -ForegroundColor DarkGray
    $deadline = (Get-Date).AddMinutes($WaitForIdpMinutes)
    while (-not (Get-NetTCPConnection -LocalPort $idpPort -State Listen -ErrorAction SilentlyContinue)) {
        if ((Get-Date) -gt $deadline) {
            Write-Host 'The IDP did not start in time; the Node.js services were not started. Run .\ps\run\Start-NodeServices.ps1 once it is up.' -ForegroundColor Red
            exit 1
        }
        Start-Sleep -Seconds 3
    }
    Write-Host 'The IDP is up.' -ForegroundColor Green
}
& (Join-Path $root 'ps\run\Start-NodeServices.ps1')