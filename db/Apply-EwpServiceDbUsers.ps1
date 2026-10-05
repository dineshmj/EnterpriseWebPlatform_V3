<#
.SYNOPSIS
    Creates or refreshes the least-privilege PostgreSQL users of EWP V3 by running
    db\EwpServiceDbUsers.sql with psql.

.DESCRIPTION
    EwpServiceDbUsers.sql creates one login role per deployable (ewp_idp, ewp_shell,
    ewp_customer_onboarding_api, ewp_customer_outbox_relay, ewp_kyc_api, ewp_documents_api,
    ewp_compliance_api, ewp_accounts_api, ewp_notifications_api) and grants each one data access to ITS OWN database only.

    It must run with psql, not pgAdmin's Query Tool: it uses psql commands (\set, \connect)
    to move from database to database. This script finds psql.exe, checks that every
    database exists, and runs the SQL file connected to the default "postgres" database.

    When to run:
      - once, after all databases have been created;
      - again after creating a NEW database (e.g. a new bounded context) or after dropping
        and recreating a database itself. Recreating only a database's tables (with its
        consolidated script) does not need a re-run.

    It is idempotent: existing roles are updated (attributes and the DEVELOPMENT password
    that matches the appsettings files), never duplicated; grants are re-applied.

    DEVELOPMENT ONLY. Outside Development, create the roles with secrets from a secret store.

.PARAMETER PsqlPath
    Full path of psql.exe. Default: PostgreSQL 18's standard install location, or psql on PATH.

.PARAMETER PostgresPassword
    Password of the "postgres" superuser. Default: the local development password "admin".

.EXAMPLE
    .\db\Apply-EwpServiceDbUsers.ps1

.EXAMPLE
    .\db\Apply-EwpServiceDbUsers.ps1 -PsqlPath 'D:\PostgreSQL\18\bin\psql.exe' -PostgresPassword 'secret'
#>
[CmdletBinding()]
param(
    [string] $PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',

    [string] $PostgresPassword = 'admin',

    [string] $HostName = 'localhost',

    [int] $Port = 5432
)

$ErrorActionPreference = 'Stop'

$sqlFile = Join-Path $PSScriptRoot 'EwpServiceDbUsers.sql'

$requiredDatabases = @(
    'EwpIdentityAccessDb',
    'EwpBssShellDb',
    'EwpCustomerDb',
    'EwpKycDb',
    'EwpDocumentsManagementDb',
    'EwpComplianceDb',
    'EwpAccountsDb',
    'EwpNotificationsDb'
)

# ---------------------------------------------------------------------------------------
# psql
# ---------------------------------------------------------------------------------------
if (-not (Test-Path $PsqlPath)) {
    $onPath = Get-Command psql.exe -ErrorAction SilentlyContinue
    if (-not $onPath) { throw "psql.exe not found at '$PsqlPath' or on PATH. Pass -PsqlPath." }
    $PsqlPath = $onPath.Source
}

if (-not (Test-Path $sqlFile)) { throw "SQL file not found: $sqlFile" }

$previousPassword = $env:PGPASSWORD
$env:PGPASSWORD = $PostgresPassword

try {
    # -----------------------------------------------------------------------------------
    # Every database must exist: the SQL stops at the first one that does not.
    # -----------------------------------------------------------------------------------
    $existing = & $PsqlPath -h $HostName -p $Port -U postgres -d postgres -At -c 'SELECT datname FROM pg_database;'
    if ($LASTEXITCODE -ne 0) { throw "Cannot connect to PostgreSQL on ${HostName}:$Port as postgres." }

    $missing = $requiredDatabases | Where-Object { $_ -notin $existing }
    if ($missing) {
        Write-Host 'These databases do not exist yet - create them first (ReadMe.txt, section 3e):' -ForegroundColor Red
        $missing | ForEach-Object { Write-Host "    CREATE DATABASE `"$_`";" -ForegroundColor Red }
        exit 1
    }

    # -----------------------------------------------------------------------------------
    # Run the SQL (it sets ON_ERROR_STOP itself; repeated here so a failure exits non-zero).
    # -----------------------------------------------------------------------------------
    Write-Host "Applying $sqlFile ..." -ForegroundColor Cyan
    & $PsqlPath -h $HostName -p $Port -U postgres -d postgres -v ON_ERROR_STOP=1 -f $sqlFile
    if ($LASTEXITCODE -ne 0) { throw "EwpServiceDbUsers.sql failed (psql exit code $LASTEXITCODE)." }

    # -----------------------------------------------------------------------------------
    # Show the result.
    # -----------------------------------------------------------------------------------
    Write-Host "`nService roles:" -ForegroundColor Green
    & $PsqlPath -h $HostName -p $Port -U postgres -d postgres -c "SELECT rolname, rolcanlogin, rolsuper FROM pg_roles WHERE rolname LIKE 'ewp\_%' ORDER BY rolname;"
}
finally {
    $env:PGPASSWORD = $previousPassword
}