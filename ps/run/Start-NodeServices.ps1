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

    Each web app is started after its Journey API is listening (it calls it).
    To DEBUG one of them: close its window (or Ctrl+C), then run it from VS Code - see ReadMe.txt
    section 6c. The others keep running.

.EXAMPLE
    .\ps\run\Start-NodeServices.ps1                    # start all five
    .\ps\run\Start-NodeServices.ps1 -Only Audit        # only the two Audit services
    .\ps\run\Start-NodeServices.ps1 -Only Operations   # only the two Operations services
    .\ps\run\Start-NodeServices.ps1 -Stop              # stop all five (the processes listening on their ports)
#>
param(
    [ValidateSet('All', 'Kyc', 'Audit', 'Operations')]
    [string] $Only = 'All',
    [switch] $Stop
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
) | Where-Object { $Only -eq 'All' -or $_.Group -eq $Only }

function Get-Listener([int] $port) {
    Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue | Select-Object -First 1
}

if ($Stop) {
    foreach ($s in $services) {
        $listener = Get-Listener $s.Port
        if ($listener) {
            Stop-Process -Id $listener.OwningProcess -Force
            Write-Host "Stopped $($s.Name) (port $($s.Port))." -ForegroundColor Yellow
        } else {
            Write-Host "$($s.Name) was not running." -ForegroundColor DarkGray
        }
    }
    return
}

# The Audit and Operations services read the development certificate from the user profile.
if (($services.Group -contains 'Audit' -or $services.Group -contains 'Operations') -and -not (Test-Path "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pfx")) {
    Write-Host 'The development certificate is not exported yet. Run once:' -ForegroundColor Red
    Write-Host '  dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pfx" -p "dev-password"'
    Write-Host '  dotnet dev-certs https -ep "$env:USERPROFILE\.aspnet\https\ewp-v3-dev.pem" --format Pem --no-password'
    exit 1
}

foreach ($s in $services) {
    if (Get-Listener $s.Port) {
        Write-Host "$($s.Name) is already running on port $($s.Port)." -ForegroundColor DarkGray
        continue
    }

    # A web app calls its Journey API: give the Journey API time to listen first.
    $journey = @{ 'Audit web' = @('Audit Journey API', 46379); 'Operations web' = @('Operations Journey API', 46382) }[$s.Name]
    if ($journey -and $services.Name -contains $journey[0]) {
        Write-Host "Waiting for the $($journey[0])..." -ForegroundColor DarkGray
        $deadline = (Get-Date).AddMinutes(3)
        while (-not (Get-Listener $journey[1]) -and (Get-Date) -lt $deadline) { Start-Sleep -Seconds 2 }
    }

    $folder = Join-Path $root $s.Folder
    Start-Process -FilePath 'cmd.exe' -WorkingDirectory $folder `
        -ArgumentList '/k', "title $($s.Name) && runnow.bat"
    Write-Host "Started $($s.Name) in its own window (port $($s.Port))." -ForegroundColor Green
}

Write-Host "`nEach window builds on its first run, so allow a minute. Stop them all with: .\ps\run\Start-NodeServices.ps1 -Stop"