<#
.SYNOPSIS
    Starts (or stops) the platform's Node.js services - the ones that are not in the Visual Studio
    launch profile - each in its own console window.

.DESCRIPTION
    Start the .NET solution in Visual Studio first (IDP, APIs, BFFs, workers), then run this.
    It opens one window per service, each running that service's runnow.bat:

        Customer KYC BFF (NestJS + Next.js)   https://kyc.dev.localhost:33800
        Audit Journey API (NestJS)            https://audit-journey.dev.localhost:46379
        Audit web (Next.js SPA + light BFF)   https://audit.dev.localhost:46380
        Operations Journey API (NestJS)       https://operations-journey.dev.localhost:46382
        Operations web (Next.js + light BFF)  https://operations.dev.localhost:46381
        Partner Products Journey API (NestJS) https://partner-products-journey.dev.localhost:46385
        Partner Products web (Next.js + BFF)  https://ewp-partner.dev.localhost:46384
        Partner Products Worker (Temporal)    health https://localhost:46383/health (needs Temporal on localhost:7233)

    The fault proxy (Toxiproxy) is NOT started here: like Kafka and Temporal it is platform plumbing
    that the .NET services use too - start it before the solution with ps\toxiproxy\Start-Toxiproxy.ps1
    (and stop it there with -Stop). This script only warns when it is not running.

    Each web app is started after its Journey API is listening (it calls it).
    To DEBUG one of them: close its window (or Ctrl+C), then run it from VS Code - see ReadMe.txt
    section 6c. The others keep running.

.EXAMPLE
    .\ps\run\Start-NodeServices.ps1                    # start all eight
    .\ps\run\Start-NodeServices.ps1 -Only Audit        # only the two Audit services
    .\ps\run\Start-NodeServices.ps1 -Only Operations   # only the two Operations services
    .\ps\run\Start-NodeServices.ps1 -Only PartnerProducts   # only the three Partner Products services
    .\ps\run\Start-NodeServices.ps1 -Only PartnerProductsWorker -Stop   # only the Temporal worker (the Digital Gold
                                                       # deviation runs stop and start it this way)
    .\ps\run\Start-NodeServices.ps1 -Stop              # stop all eight (the processes listening on their ports) and close
                                                       # their windows; the fault proxy, Kafka and Temporal keep running
    .\ps\run\Start-NodeServices.ps1 -Restart           # stop, wait until the ports are free, start again (picks up changed code;
                                                       # the web apps rebuild as they start). Works with -Only too.
#>
param(
    [ValidateSet('All', 'Kyc', 'Audit', 'Operations', 'PartnerProducts', 'PartnerProductsWorker')]
    [string] $Only = 'All',
    [switch] $Stop,
    [switch] $Restart
)

cls

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path   # the repository root (this script is in ps\<area>)

$services = @(
    [pscustomobject]@{ Name = 'Customer KYC BFF';  Group = 'Kyc';   Port = 33800; Folder = 'src\Microservices\CustomerKyc\BFF.Web' }
    [pscustomobject]@{ Name = 'Audit Journey API'; Group = 'Audit'; Port = 46379; Folder = 'src\Microservices\Audit\JourneyApi' }
    [pscustomobject]@{ Name = 'Audit web';         Group = 'Audit'; Port = 46380; Folder = 'src\Microservices\Audit\BFF.Web' }
    [pscustomobject]@{ Name = 'Operations Journey API'; Group = 'Operations'; Port = 46382; Folder = 'src\Microservices\Operations\JourneyApi' }
    [pscustomobject]@{ Name = 'Operations web';         Group = 'Operations'; Port = 46381; Folder = 'src\Microservices\Operations\BFF.Web' }
    [pscustomobject]@{ Name = 'Partner Products Journey API'; Group = 'PartnerProducts'; Port = 46385; Folder = 'src\Microservices\PartnerProducts\JourneyApi' }
    [pscustomobject]@{ Name = 'Partner Products web';         Group = 'PartnerProducts'; Port = 46384; Folder = 'src\Microservices\PartnerProducts\BFF.Web' }
    # ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
    # │ The Temporal worker: its port is its health probe (it serves nothing else). Start
    # │ Temporal first (ps\temporal\Start-Temporal.ps1); -Stop stops it by this port.
    # └──────────────────────────────────────────────────────────────────────────────────────
    [pscustomobject]@{ Name = 'Partner Products Worker';      Group = 'PartnerProducts'; Port = 46383; Folder = 'src\Microservices\PartnerProducts\Worker' }
) | Where-Object { $Only -eq 'All' -or $_.Group -eq $Only -or ($Only -eq 'PartnerProductsWorker' -and $_.Name -eq 'Partner Products Worker') }

function Get-Listener([int] $port) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
}

# The command line each service window is started with (below) - also how -Stop finds that window.
# ".\runnow.bat", not "runnow.bat": with NoDefaultCurrentDirectoryInExePath set (some tools set it),
# cmd.exe does not look in the current folder for a bare name, and the window would start nothing.
function Get-WindowCommand([string] $name) { "title $name && .\runnow.bat" }

# ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
# │ Each service runs in "cmd /k ...", which keeps its window open after the service has
# │ stopped. So -Stop ends the window's own cmd.exe with its whole process tree (pnpm,
# │ node); the console window then closes by itself. Found by its exact command line, so
# │ the Kafka, Temporal and fault proxy windows - and any other console - are never touched.
# └──────────────────────────────────────────────────────────────────────────────────────
function Close-ServiceWindow([string] $name) {
    $command = Get-WindowCommand $name
    $windows = @(Get-CimInstance Win32_Process -Filter "Name = 'cmd.exe'" -ErrorAction SilentlyContinue |
        Where-Object { $_.CommandLine -and $_.CommandLine.TrimEnd().EndsWith($command) })   # Windows adds a trailing space
    foreach ($window in $windows) {
        & taskkill.exe /PID $window.ProcessId /T /F *> $null
    }
    return $windows.Count
}

# The fault proxy's admin port (ps\toxiproxy\Start-Toxiproxy.ps1 starts it; only checked here).
$faultProxyPort = 8474

if ($Stop -and $Restart) {
    Write-Host 'Use -Stop or -Restart, not both.' -ForegroundColor Red
    exit 1
}

if ($Stop -or $Restart) {
    foreach ($s in $services) {
        $listener = Get-Listener $s.Port
        if ($listener) {
            Stop-Process -Id $listener.OwningProcess -Force
        }
        # Also a window whose service had already ended (e.g. after an error).
        $closed = Close-ServiceWindow $s.Name
        if ($listener) {
            Write-Host "Stopped $($s.Name) (port $($s.Port))$(if ($closed) { ' and closed its window' })." -ForegroundColor Yellow
        } elseif ($closed) {
            Write-Host "$($s.Name) was not running; closed its window." -ForegroundColor Yellow
        } else {
            Write-Host "$($s.Name) was not running." -ForegroundColor DarkGray
        }
    }
    if ($Stop) { return }

    # ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
    # │ -Restart: wait until every port is free, or the start below would see the old process
    # │ still listening and skip that service - leaving the OLD code running.
    # └──────────────────────────────────────────────────────────────────────────────────────
    $ports = @($services.Port)
    $deadline = (Get-Date).AddSeconds(30)
    while (($ports | Where-Object { Get-Listener $_ }) -and (Get-Date) -lt $deadline) { Start-Sleep -Milliseconds 500 }
    $stillBusy = @($ports | Where-Object { Get-Listener $_ })
    if ($stillBusy.Count -gt 0) {
        Write-Host "Still in use after 30 s: port(s) $($stillBusy -join ', '). Stop them by hand, then start again." -ForegroundColor Red
        exit 1
    }
    Write-Host 'All stopped. Starting again...' -ForegroundColor Yellow
}

# The Audit, Operations and Partner Products services read the development certificate from the user profile.
if (($services.Group -contains 'Audit' -or $services.Group -contains 'Operations' -or $services.Group -contains 'PartnerProducts') -and -not (Test-Path "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pfx")) {
    Write-Host 'The development certificate is not exported yet. Run once:' -ForegroundColor Red
    Write-Host '  dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pfx" -p "dev-password"'
    Write-Host '  dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pem" --format Pem --no-password'
    exit 1
}

# ┌─ ARCH ───────────────────────────────────────────────────────────────────────────────
# │ The fault proxy is started on its own (Start-Toxiproxy.ps1), like Kafka and Temporal:
# │ the .NET services call through it too, and it must not go down when these restart.
# │ Without it every server-to-server call in Development fails - so say so, loudly.
# └──────────────────────────────────────────────────────────────────────────────────────
if (-not (Get-Listener $faultProxyPort)) {
    Write-Host 'The fault proxy is not running: every call to an API through it will fail. Start it with:' -ForegroundColor Red
    Write-Host '  .\ps\toxiproxy\Start-Toxiproxy.ps1' -ForegroundColor Red
}

foreach ($s in $services) {
    if (Get-Listener $s.Port) {
        Write-Host "$($s.Name) is already running on port $($s.Port)." -ForegroundColor DarkGray
        continue
    }

    # A web app calls its Journey API: give the Journey API time to listen first.
    $journey = @{
        'Audit web'            = @('Audit Journey API', 46379)
        'Operations web'       = @('Operations Journey API', 46382)
        'Partner Products web' = @('Partner Products Journey API', 46385)
    }[$s.Name]
    if ($journey -and $services.Name -contains $journey[0]) {
        Write-Host "Waiting for the $($journey[0])..." -ForegroundColor DarkGray
        $deadline = (Get-Date).AddMinutes(3)
        while (-not (Get-Listener $journey[1]) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    }

    $folder = Join-Path $root $s.Folder
    Start-Process -FilePath 'cmd.exe' -WorkingDirectory $folder `
        -ArgumentList '/k', (Get-WindowCommand $s.Name)
    Write-Host "Started $($s.Name) in its own window (port $($s.Port))." -ForegroundColor Green
}

Write-Host "`nEach window builds on its first run, so allow a minute. Stop them all with: .\ps\run\Start-NodeServices.ps1 -Stop"