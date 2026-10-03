<#
.SYNOPSIS
    Scans every EWP V3 dependency for known vulnerabilities (OWASP A06:
    Vulnerable and Outdated Components).

.DESCRIPTION
    - .NET: `dotnet list package --vulnerable --include-transitive` over the solution
      (GitHub Advisory Database via nuget.org).
    - Node: `pnpm audit` in the KYC BFF and the three Next.js client apps.

    Exits with code 1 when a vulnerability at or above -FailOn is found in anything
    that is deployed (.NET packages, production npm dependencies), so it can gate a
    CI pipeline. Advisories in build tooling (npm dev dependencies) are reported as
    warnings. Needs network access to nuget.org and the npm registry.

.EXAMPLE
    .\Scan-Dependencies.ps1
    .\Scan-Dependencies.ps1 -FailOn critical
#>
param(
    [ValidateSet('low', 'moderate', 'high', 'critical')]
    [string] $FailOn = 'high'
)

$ErrorActionPreference = 'Stop'
$root = $PSScriptRoot
$failed = $false
$severityRank = @{ low = 1; moderate = 2; high = 3; critical = 4 }

function Invoke-Native([string] $commandLine, [string] $workingDirectory) {
    # cmd.exe keeps native stderr from becoming PowerShell NativeCommandError records.
    Push-Location $workingDirectory
    try { cmd.exe /d /c "$commandLine 2>&1" } finally { Pop-Location }
}

Write-Host "`n=== .NET packages (solution) ===" -ForegroundColor Cyan
$dotnetOutput = Invoke-Native 'dotnet list EnterpriseWebPlatform.BSS.sln package --vulnerable --include-transitive' $root
$dotnetOutput | ForEach-Object { Write-Host $_ }
foreach ($line in $dotnetOutput) {
    if ($line -match '\b(Low|Moderate|High|Critical)\b' -and $severityRank[$Matches[1].ToLowerInvariant()] -ge $severityRank[$FailOn]) {
        $failed = $true
    }
}

$nodeProjects = @(
    'src\Shell\client-app',
    'src\Microservices\CustomerOnboarding\BFF.Web\client-app',
    'src\Microservices\CustomerKyc\BFF.Web',
    'src\Microservices\CustomerKyc\BFF.Web\client-app'
)

foreach ($project in $nodeProjects) {
    $path = Join-Path $root $project
    if (-not (Test-Path (Join-Path $path 'package.json'))) { continue }

    # Gate: production dependencies (what is deployed and runs).
    Write-Host "`n=== pnpm audit (production): $project ===" -ForegroundColor Cyan
    $output = Invoke-Native "pnpm audit --prod --audit-level $FailOn" $path
    $output | ForEach-Object { Write-Host $_ }
    if ($LASTEXITCODE -ne 0) { $failed = $true }

    # Report only: build tooling (ESLint, Nest CLI, ...), never deployed.
    $devOutput = Invoke-Native "pnpm audit --dev --audit-level $FailOn" $path
    if ($LASTEXITCODE -ne 0) {
        $summary = ($devOutput | Where-Object { $_ -match 'Severity:' }) -join ' '
        Write-Host "  Build tooling (dev dependencies) has advisories: $summary - update when convenient; not deployed." -ForegroundColor Yellow
    }
}

if ($failed) {
    Write-Host "`nVulnerabilities at or above '$FailOn' were found." -ForegroundColor Red
    exit 1
}

Write-Host "`nNo vulnerabilities at or above '$FailOn' were found." -ForegroundColor Green
