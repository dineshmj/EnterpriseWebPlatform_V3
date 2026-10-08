<#
.SYNOPSIS
    Creates every EWP V3 database, its least-privilege users, and its tables and demo data - in the
    one order that works - with a single command. For a NEW laptop, or to start again from scratch.

.DESCRIPTION
    1. Creates each database that does not exist yet (11 of them).
    2. Creates / refreshes the service users (Apply-EwpServiceDbUsers.ps1) - BEFORE the scripts, so
       the grants inside the scripts find their users (no "Role ... does not exist" warnings).
    3. Runs every database's consolidated script, each connected to ITS OWN database.

    It ERASES the data of every database it runs a script for, so it asks first (or pass -Force).
    -Database limits steps 1 and 3 to the named databases (e.g. -Database EwpAuditDb).

    After recreating the CO, KYC, Compliance, Accounts or Payments database on a machine that already
    has Kafka messages, recreate the topics too (ReadMe.txt, "Upgrading an existing set-up").

.EXAMPLE
    .\ps\database\Initialize-EwpDatabases.ps1
.EXAMPLE
    .\ps\database\Initialize-EwpDatabases.ps1 -Database EwpBffStateDb -Force
#>
[CmdletBinding()]
param(
    [string[]] $Database,
    [switch] $Force,
    [string] $PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',
    [string] $PostgresPassword = 'admin',
    [string] $HostName = 'localhost',
    [int] $Port = 5432
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..\..')).Path   # the repository root (this script is in ps\database)

# Every database and its ONE consolidated script, in a safe order (EwpBffStateDb last: it grants the BFF users).
$databases = [ordered]@{
    'EwpIdentityAccessDb'      = 'src\IDP\IdentityAccessDB\IdentityAccessDb.sql'
    'EwpBssShellDb'            = 'src\Shell\MenuDB\EwpBssShellDb.sql'
    'EwpCustomerDb'            = 'src\Microservices\CustomerOnboarding\API\CustomerDB\EwpCustomerDb.sql'
    'EwpKycDb'                 = 'src\Microservices\CustomerKyc\API\KycDb\EwpKycDb.sql'
    'EwpDocumentsManagementDb' = 'src\Microservices\DocumentsManagement\API\DocumentMgmtDB\EwpDocumentsManagementDb.sql'
    'EwpComplianceDb'          = 'src\Microservices\Compliance\API\ComplianceDb\EwpComplianceDb.sql'
    'EwpAccountsDb'            = 'src\Microservices\Accounts\API\AccountsDb\EwpAccountsDb.sql'
    'EwpNotificationsDb'       = 'src\Microservices\Notifications\API\NotificationsDb\EwpNotificationsDb.sql'
    'EwpPaymentsDb'            = 'src\Microservices\Payments\API\PaymentsDb\EwpPaymentsDb.sql'
    'EwpAuditDb'               = 'src\Microservices\Audit\API\AuditDb\EwpAuditDb.sql'
    'EwpBffStateDb'            = 'db\EwpBffStateDb.sql'
}

$selected = if ($Database) { $Database } else { @($databases.Keys) }
$unknown = $selected | Where-Object { -not $databases.Contains($_) }
if ($unknown) { throw "Unknown database(s): $($unknown -join ', '). Known: $($databases.Keys -join ', ')." }

if (-not (Test-Path $PsqlPath)) {
    $onPath = Get-Command psql.exe -ErrorAction SilentlyContinue
    if (-not $onPath) { throw "psql.exe not found at '$PsqlPath' or on PATH. Pass -PsqlPath." }
    $PsqlPath = $onPath.Source
}

if (-not $Force) {
    Write-Host "This ERASES and recreates the data of: $($selected -join ', ')." -ForegroundColor Yellow
    if ((Read-Host 'Type YES to continue') -ne 'YES') { Write-Host 'Nothing changed.'; exit 1 }
}

$previousPassword = $env:PGPASSWORD
$env:PGPASSWORD = $PostgresPassword
try {
    # 1. Databases (the quotes keep the mixed-case names the connection strings use).
    $existing = & $PsqlPath -h $HostName -p $Port -U postgres -d postgres -At -c 'SELECT datname FROM pg_database;'
    if ($LASTEXITCODE -ne 0) { throw "Cannot connect to PostgreSQL on ${HostName}:$Port as postgres." }
    foreach ($name in $databases.Keys | Where-Object { $_ -notin $existing }) {
        & $PsqlPath -h $HostName -p $Port -U postgres -d postgres -q -c "CREATE DATABASE `"$name`";"
        if ($LASTEXITCODE -ne 0) { throw "CREATE DATABASE $name failed." }
        Write-Host "Created database $name." -ForegroundColor Green
    }

    # 2. Users and grants - before the scripts.
    & (Join-Path $PSScriptRoot 'Apply-EwpServiceDbUsers.ps1') -PsqlPath $PsqlPath -PostgresPassword $PostgresPassword -HostName $HostName -Port $Port | Out-Null
    Write-Host 'Service users created / refreshed.' -ForegroundColor Green

    # 3. Each database's script, connected to that database. Warnings would mean a missing user.
    foreach ($name in $databases.Keys | Where-Object { $_ -in $selected }) {
        $script = Join-Path $root $databases[$name]
        Write-Host "Running $($databases[$name]) on $name ..." -ForegroundColor Cyan
        # Windows PowerShell turns a native program's stderr into errors when redirected: relax the
        # error mode for this call only, and keep PostgreSQL's NOTICEs (e.g. "drop cascades") quiet.
        $ErrorActionPreference = 'Continue'
        $env:PGOPTIONS = '--client-min-messages=warning'
        $output = & $PsqlPath -h $HostName -p $Port -U postgres -d $name -q -v ON_ERROR_STOP=1 -f $script 2>&1
        $exitCode = $LASTEXITCODE
        $env:PGOPTIONS = $null
        $ErrorActionPreference = 'Stop'
        if ($exitCode -ne 0) { $output | Select-Object -Last 15 | Out-Host; throw "$($databases[$name]) failed on $name." }
        $output | Where-Object { "$_" -match 'WARNING' } | ForEach-Object { Write-Host "  $_" -ForegroundColor Yellow }
    }

    Write-Host "`nDone: $($selected.Count) database(s) recreated." -ForegroundColor Green
}
finally {
    $env:PGPASSWORD = $previousPassword
}