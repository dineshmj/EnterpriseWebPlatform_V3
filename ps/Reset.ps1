<#
.SYNOPSIS
    Resets the whole local EWP V3 platform to a clean start: every database, every PostgreSQL user,
    Kafka (all data, users, topics, consumer groups and ACLs), the stored documents and the test
    sign-ins. One command, no connection strings to type.
.DESCRIPTION
    Run it from the repository root with PostgreSQL running. It asks once (or pass -Force), then:

      1. Stops the platform: the Node.js services (Start-NodeServices.ps1 -Stop) and every process
         listening on a platform port (the IDP, APIs, BFFs, workers and simulators - Visual Studio's
         debugging session simply ends). Databases cannot be dropped while services hold connections.
      2. Stops Kafka (kafka\Stop-Kafka.ps1).
      3. Wipes Kafka's data folder (log.dirs in C:\Kafka\config\server.properties): every topic,
         message, consumer group, user and ACL is gone - nothing stale, no unknown topics or groups.
      4. Formats Kafka's storage again (KRaft) with a new cluster ID and the "admin" SCRAM user
         planted at format time (the broker authenticates to itself as admin; its password is read
         from C:\Kafka\config\admin.properties).
      5. Starts Kafka in its own window and waits until it accepts the admin user.
      6. Recreates the Kafka users, the topics and the consumer groups (kafka\Setup-KafkaSecurity.ps1
         -Phase Prepare), then the ACLs (-Phase Acls), and checks that exactly the platform's topics
         exist.
      7. Drops the 11 EWP databases (WITH FORCE) and every ewp_* PostgreSQL user.
      8. Creates the databases, the users with their grants, and all tables and seed data again
         (database\Initialize-EwpDatabases.ps1 -Force).
      9. Empties Documents Management's file store (the PDFs live on disk, not in its database).
     10. Deletes the Playwright saved sign-ins and test results (their sessions no longer exist).

    Afterwards: start the solution in Visual Studio (this also restarts the simulators, whose state
    is in memory), then .\ps\run\Start-NodeServices.ps1. In the browser every old sign-in is gone:
    sign in again (with Mfa:Enabled on, everybody enrols again). The demo customers are re-created by
    the Playwright seed scenarios.

    DEVELOPMENT ONLY. It ERASES all platform data on this machine.
.EXAMPLE
    .\ps\Reset.ps1
.EXAMPLE
    .\ps\Reset.ps1 -Force
#>
param(
    [switch] $Force,
    [string] $KafkaHome = 'C:\Kafka',
    [string] $PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',
    [string] $PostgresPassword = 'admin',
    [string] $HostName = 'localhost',
    [int] $Port = 5432
)

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path   # the repository root (this script is in ps\)

$databases = @(
    'EwpIdentityAccessDb', 'EwpBssShellDb', 'EwpCustomerDb', 'EwpKycDb', 'EwpDocumentsManagementDb',
    'EwpComplianceDb', 'EwpAccountsDb', 'EwpNotificationsDb', 'EwpPaymentsDb', 'EwpAuditDb', 'EwpBffStateDb'
)

# Every port a platform process listens on (ReadMe.txt section 3): IDP, Shell, BFFs, APIs,
# Node.js services, simulators and the workers' health ports. Not Kafka (9092/9093) or PostgreSQL.
$platformPorts = @(
    46392, 46367, 46311, 46363, 33800, 46305, 49486, 46399, 46306, 45456, 48486,
    46377, 44488, 46388, 46378, 46379, 46380, 46366, 46376, 46386,
    5101, 5102, 5103, 5104, 5105, 5106, 5107, 5108, 5109
)

function Write-Step([string] $Text) { Write-Host "`n=== $Text" -ForegroundColor Cyan }

function Invoke-Psql([string] $Database, [string] $Sql) {
    $output = & $PsqlPath -h $HostName -p $Port -U postgres -d $Database -v ON_ERROR_STOP=1 -At -c $Sql 2>&1
    if ($LASTEXITCODE -ne 0) { throw "psql failed on ${Database}:`n$($output -join "`n")" }
    return $output
}

function Invoke-KafkaTool([string] $Tool, [string] $Arguments) {
    # Run from an empty folder: the Java launcher expands "*" against the current folder.
    $work = Join-Path $env:TEMP 'ewp-kafka-tools'
    New-Item -ItemType Directory -Force $work | Out-Null
    $exe = Join-Path $KafkaHome "bin\windows\$Tool"
    $output = cmd.exe /d /c "cd /d `"$work`" && `"$exe`" $Arguments 2>&1"
    return @{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

# ---------------------------------------------------------------------------------------------------
# Pre-flight: everything this needs is where it should be - before anything is stopped or erased.
# ---------------------------------------------------------------------------------------------------
if (-not (Test-Path $PsqlPath)) {
    $onPath = Get-Command psql.exe -ErrorAction SilentlyContinue
    if (-not $onPath) { throw "psql.exe not found at '$PsqlPath' or on PATH. Pass -PsqlPath." }
    $PsqlPath = $onPath.Source
}
$serverProps = Join-Path $KafkaHome 'config\server.properties'
$adminProps = Join-Path $KafkaHome 'config\admin.properties'
foreach ($required in @($serverProps, $adminProps)) {
    if (-not (Test-Path $required)) {
        throw "$required not found. Kafka must have been secured once (ps\kafka\Setup-KafkaSecurity.ps1, ReadMe.txt section 5)."
    }
}
$logDirs = ((Get-Content $serverProps | Where-Object { $_ -match '^\s*log\.dirs\s*=' } | Select-Object -First 1) -split '=', 2)[1]
if (-not $logDirs) { throw "log.dirs is not set in $serverProps." }
$logDirs = $logDirs.Trim().Replace('\\', '\')
$adminPassword = [regex]::Match((Get-Content $adminProps -Raw), 'password="([^"]+)"').Groups[1].Value
if (-not $adminPassword) { throw "The admin password was not found in $adminProps." }

$env:PGPASSWORD = $PostgresPassword
& $PsqlPath -h $HostName -p $Port -U postgres -d postgres -At -c 'SELECT 1;' *> $null
if ($LASTEXITCODE -ne 0) { throw "Cannot connect to PostgreSQL on ${HostName}:$Port as postgres. Is it running?" }

$storage = Join-Path $root 'src\Microservices\DocumentsManagement\API\DocumentStorage'
$playwright = Join-Path $root 'tst\playwright'

if (-not $Force) {
    Write-Host 'This ERASES all local EWP V3 data and starts again from scratch:' -ForegroundColor Yellow
    Write-Host "  - stops the running platform (Visual Studio's session, the Node.js services) and Kafka"
    Write-Host "  - Kafka: everything in $logDirs (topics, messages, consumer groups, users, ACLs)"
    Write-Host "  - PostgreSQL: the 11 EWP databases and every ewp_* user"
    Write-Host "  - the stored documents ($storage)"
    Write-Host '  - the Playwright saved sign-ins and test results'
    if ((Read-Host 'Type YES to continue') -ne 'YES') { Write-Host 'Nothing changed.'; exit 1 }
}

# ---------------------------------------------------------------------------------------------------
Write-Step '1/10  Stopping the platform'
& (Join-Path $PSScriptRoot 'run\Start-NodeServices.ps1') -Stop | Out-Null
$owners = @(Get-NetTCPConnection -LocalPort $platformPorts -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess | Where-Object { $_ -and $_ -ne 0 } | Sort-Object -Unique)
foreach ($id in $owners) {
    $p = Get-Process -Id $id -ErrorAction SilentlyContinue
    if ($p) { Write-Host "     stopping $($p.ProcessName) (PID $id)"; Stop-Process -Id $id -Force }
}
Write-Host "     $($owners.Count) process(es) stopped."

Write-Step '2/10  Stopping Kafka'
& (Join-Path $PSScriptRoot 'kafka\Stop-Kafka.ps1') -KafkaHome $KafkaHome

Write-Step "3/10  Wiping Kafka's data ($logDirs)"
if (Test-Path $logDirs) {
    Get-ChildItem $logDirs -Force | Remove-Item -Recurse -Force
}
New-Item -ItemType Directory -Force $logDirs | Out-Null
Write-Host '     empty.'

Write-Step "4/10  Formatting Kafka's storage (new cluster ID, admin user planted)"
$uuid = Invoke-KafkaTool 'kafka-storage.bat' 'random-uuid'
$clusterId = ($uuid.Output -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[A-Za-z0-9_-]{22}$' } | Select-Object -Last 1)
if (-not $clusterId) { throw "kafka-storage random-uuid gave no cluster ID:`n$($uuid.Output)" }
$format = Invoke-KafkaTool 'kafka-storage.bat' "format --standalone -t $clusterId -c `"$serverProps`" --add-scram `"SCRAM-SHA-512=[name=admin,password=$adminPassword]`""
if ($format.ExitCode -ne 0) { throw "kafka-storage format failed:`n$($format.Output)" }
Write-Host "     cluster ID $clusterId"

Write-Step '5/10  Starting Kafka (its own window) and waiting for it'
$startBat = Join-Path $KafkaHome 'StartKafka.bat'
if (Test-Path $startBat) {
    Start-Process -FilePath $startBat -WorkingDirectory $KafkaHome
} else {
    Start-Process -FilePath 'cmd.exe' -ArgumentList '/k', 'bin\windows\kafka-server-start.bat config\server.properties' -WorkingDirectory $KafkaHome
}
$deadline = (Get-Date).AddSeconds(120)
$ready = $false
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 3
    $probe = Invoke-KafkaTool 'kafka-topics.bat' "--bootstrap-server localhost:9092 --command-config `"$adminProps`" --list"
    if ($probe.ExitCode -eq 0) { $ready = $true; break }
}
if (-not $ready) { throw 'Kafka did not accept the admin user within 120 s. Look at the Kafka window for the reason.' }
Write-Host '     Kafka is running and accepts the admin user.'

Write-Step '6/10  Kafka users, topics, consumer groups and ACLs'
& (Join-Path $PSScriptRoot 'kafka\Setup-KafkaSecurity.ps1') -Phase Prepare -KafkaHome $KafkaHome
& (Join-Path $PSScriptRoot 'kafka\Setup-KafkaSecurity.ps1') -Phase Acls -KafkaHome $KafkaHome | Out-Null
$topics = (Invoke-KafkaTool 'kafka-topics.bat' "--bootstrap-server localhost:9092 --command-config `"$adminProps`" --list").Output -split "`n" |
    ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notlike '__*' }
Write-Host "     $($topics.Count) topics, ACLs applied (internal topics such as __consumer_offsets not counted)."

Write-Step '7/10  Dropping the EWP databases and users'
foreach ($name in $databases) {
    Invoke-Psql 'postgres' "DROP DATABASE IF EXISTS `"$name`" WITH (FORCE);" | Out-Null
    Write-Host "     $name dropped"
}
$roles = @(Invoke-Psql 'postgres' "SELECT rolname FROM pg_roles WHERE rolname LIKE 'ewp\_%' ORDER BY 1;" | Where-Object { $_ })
foreach ($role in $roles) {
    Invoke-Psql 'postgres' "DROP OWNED BY `"$role`"; DROP ROLE `"$role`";" | Out-Null
}
Write-Host "     $($roles.Count) ewp_* user(s) dropped."

Write-Step '8/10  Creating the databases, users, grants, tables and seed data'
& (Join-Path $PSScriptRoot 'database\Initialize-EwpDatabases.ps1') -Force -PsqlPath $PsqlPath -PostgresPassword $PostgresPassword -HostName $HostName -Port $Port

Write-Step "9/10  Emptying Documents Management's file store"
if (Test-Path $storage) {
    $files = @(Get-ChildItem $storage -Force | Where-Object { $_.Name -ne '.gitignore' })
    $files | Remove-Item -Recurse -Force
    Write-Host "     $($files.Count) item(s) removed (.gitignore kept)."
}

Write-Step '10/10  Clearing the Playwright sign-ins and results'
foreach ($folder in @('.auth', 'test-results', 'playwright-report')) {
    $path = Join-Path $playwright $folder
    if (Test-Path $path) { Remove-Item $path -Recurse -Force; Write-Host "     tst\playwright\$folder removed" }
}

Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
Write-Host "`nThe platform is reset." -ForegroundColor Green
Write-Host 'Next: start the solution in Visual Studio, then .\ps\run\Start-NodeServices.ps1.'
Write-Host 'Sign in again in the browser (old sessions are gone); run the Playwright seed scenarios for demo customers.'