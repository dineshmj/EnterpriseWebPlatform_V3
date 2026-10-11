<#
.SYNOPSIS
    Resets the local EWP V3 platform to a clean start, at one of three levels. No connection strings
    to type: PostgreSQL is reached as the postgres superuser, Kafka as its admin user.
.DESCRIPTION
    Run it with PostgreSQL running. Without -Level it shows a menu:

      1  Data only               the 11 databases and every ewp_* user dropped and recreated (tables,
                                 grants, seed data), the stored documents emptied. Kafka is left as
                                 it is: its messages stay, but every consumer has already read them.
      2  Data + Kafka security   level 1, plus the Kafka users, topics, consumer groups and ACLs
                                 recreated or refreshed (Setup-KafkaSecurity Prepare + Acls) on the
                                 running broker - e.g. after adding a topic or a subscriber.
      3  Everything              level 2, with Kafka itself wiped and re-formatted first: every topic,
                                 message, consumer group, user and ACL gone, then recreated - nothing
                                 stale, nothing unknown. The full "new laptop" start.

    Every level first stops the platform (the Node.js services, and every process listening on a
    platform port - Visual Studio's debugging session simply ends): databases cannot be dropped while
    services hold connections. Every level also deletes the Playwright saved sign-ins and results.

    Afterwards: start the solution in Visual Studio (this also restarts the simulators, whose state is
    in memory), then .\ps\run\Start-NodeServices.ps1, and sign in again (old sessions are gone; with
    Mfa:Enabled on, everybody enrols again). The demo customers come from the Playwright seed scenarios.

    DEVELOPMENT ONLY. It ERASES platform data on this machine. It asks for YES (or pass -Force).
.EXAMPLE
    .\ps\Reset.ps1                      # menu
.EXAMPLE
    .\ps\Reset.ps1 -Level 1             # data only, asks YES
.EXAMPLE
    .\ps\Reset.ps1 -Level 3 -Force      # everything, no questions
#>
param(
    [ValidateSet(1, 2, 3)]
    [int] $Level,
    [switch] $Force,
    [string] $KafkaHome = 'C:\Kafka',
    [string] $PsqlPath = 'C:\Program Files\PostgreSQL\18\bin\psql.exe',
    [string] $PostgresPassword = 'admin',
    [string] $HostName = 'localhost',
    [int] $Port = 5432
)

# After param(): PowerShell requires the param block to be the script's first statement.
Clear-Host

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

$serverProps = Join-Path $KafkaHome 'config\server.properties'
$adminProps = Join-Path $KafkaHome 'config\admin.properties'
$storage = Join-Path $root 'src\Microservices\DocumentsManagement\API\DocumentStorage'
$playwright = Join-Path $root 'tst\playwright'

# ===================================================================================================
# Helpers
# ===================================================================================================
$script:stepNumber = 0
$script:stepCount = 0
function Write-Step([string] $Text) {
    $script:stepNumber++
    Write-Host "`n=== $($script:stepNumber)/$($script:stepCount)  $Text" -ForegroundColor Cyan
}

function Invoke-Psql([string] $Database, [string] $Sql) {
    # The SQL goes in through standard input, NOT -c: Windows PowerShell 5.1 strips the double
    # quotes inside a native program's arguments, so "EwpIdentityAccessDb" would reach PostgreSQL
    # unquoted, be folded to ewpidentityaccessdb, and match nothing. PostgreSQL's NOTICEs are kept
    # quiet, and the error mode relaxed for this call: PowerShell 5.1 turns any stderr line into a
    # terminating error when errors stop the script. Failure is judged by psql's exit code.
    $previous = $ErrorActionPreference
    $ErrorActionPreference = 'Continue'
    $env:PGOPTIONS = '--client-min-messages=warning'
    try {
        $output = $Sql | & $PsqlPath -h $HostName -p $Port -U postgres -d $Database -v ON_ERROR_STOP=1 -At -q -f - 2>&1
        $exitCode = $LASTEXITCODE
    }
    finally {
        $env:PGOPTIONS = $null
        $ErrorActionPreference = $previous
    }
    if ($exitCode -ne 0) { throw "psql failed on ${Database}:`n$($output -join "`n")" }
    return @($output | Where-Object { $_ -is [string] })
}

function Invoke-KafkaTool([string] $Tool, [string] $Arguments) {
    # Run from an empty folder: the Java launcher expands "*" against the current folder.
    $work = Join-Path $env:TEMP 'ewp-kafka-tools'
    New-Item -ItemType Directory -Force $work | Out-Null
    $exe = Join-Path $KafkaHome "bin\windows\$Tool"
    $output = cmd.exe /d /c "cd /d `"$work`" && `"$exe`" $Arguments 2>&1"
    return @{ ExitCode = $LASTEXITCODE; Output = ($output -join "`n") }
}

function Test-KafkaPort {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('localhost', 9092); return $true } catch { return $false } finally { $client.Dispose() }
}

function Test-KafkaAdmin {
    # The port first: without a broker, kafka-topics waits 60 s and - being a .bat - still exits 0.
    # So its exit code is not trusted: the output must carry no error either.
    if (-not (Test-KafkaPort)) { return $false }
    $list = Invoke-KafkaTool 'kafka-topics.bat' "--bootstrap-server localhost:9092 --command-config `"$adminProps`" --list"
    return ($list.ExitCode -eq 0 -and $list.Output -notmatch 'Error while executing|Exception')
}

# ===================================================================================================
# Steps
# ===================================================================================================
function Stop-Platform {
    Write-Step 'Stopping the platform (Node.js services, Visual Studio session, workers, simulators)'
    & (Join-Path $PSScriptRoot 'run\Start-NodeServices.ps1') -Stop | Out-Null
    $owners = @(Get-NetTCPConnection -LocalPort $platformPorts -State Listen -ErrorAction SilentlyContinue |
        Select-Object -ExpandProperty OwningProcess | Where-Object { $_ -and $_ -ne 0 } | Sort-Object -Unique)
    foreach ($id in $owners) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p) { Write-Host "     stopping $($p.ProcessName) (PID $id)"; Stop-Process -Id $id -Force }
    }
    Write-Host "     $($owners.Count) process(es) stopped."
}

function Reset-KafkaStorage {
    Write-Step 'Stopping Kafka'
    & (Join-Path $PSScriptRoot 'kafka\Stop-Kafka.ps1') -KafkaHome $KafkaHome

    Write-Step "Wiping Kafka's data ($script:logDirs)"
    if (Test-Path $script:logDirs) { Get-ChildItem $script:logDirs -Force | Remove-Item -Recurse -Force }
    New-Item -ItemType Directory -Force $script:logDirs | Out-Null
    Write-Host '     empty.'

    Write-Step "Formatting Kafka's storage (new cluster ID, admin user planted)"
    $uuid = Invoke-KafkaTool 'kafka-storage.bat' 'random-uuid'
    $clusterId = ($uuid.Output -split "`n" | ForEach-Object { $_.Trim() } | Where-Object { $_ -match '^[A-Za-z0-9_-]{22}$' } | Select-Object -Last 1)
    if (-not $clusterId) { throw "kafka-storage random-uuid gave no cluster ID:`n$($uuid.Output)" }
    $format = Invoke-KafkaTool 'kafka-storage.bat' "format --standalone -t $clusterId -c `"$serverProps`" --add-scram `"SCRAM-SHA-512=[name=admin,password=$script:adminPassword]`""
    if ($format.ExitCode -ne 0 -or $format.Output -notmatch 'Formatting') { throw "kafka-storage format failed:`n$($format.Output)" }
    Write-Host "     cluster ID $clusterId"
}

function Start-KafkaIfNeeded {
    Write-Step 'Kafka running and accepting the admin user'
    if (Test-KafkaAdmin) { Write-Host '     already running.'; return }

    $startBat = Join-Path $KafkaHome 'StartKafka.bat'
    if (Test-Path $startBat) {
        Start-Process -FilePath $startBat -WorkingDirectory $KafkaHome
    } else {
        Start-Process -FilePath 'cmd.exe' -ArgumentList '/k', 'bin\windows\kafka-server-start.bat config\server.properties' -WorkingDirectory $KafkaHome
    }
    Write-Host '     started in its own window; waiting ...'
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if (Test-KafkaAdmin) { Write-Host '     Kafka is running and accepts the admin user.'; return }
    }
    throw 'Kafka did not accept the admin user within 120 s. Look at the Kafka window for the reason.'
}

function Restore-KafkaSecurity {
    Write-Step 'Kafka users, topics, consumer groups and ACLs'
    & (Join-Path $PSScriptRoot 'kafka\Setup-KafkaSecurity.ps1') -Phase Prepare -KafkaHome $KafkaHome
    & (Join-Path $PSScriptRoot 'kafka\Setup-KafkaSecurity.ps1') -Phase Acls -KafkaHome $KafkaHome | Out-Null
    $topics = (Invoke-KafkaTool 'kafka-topics.bat' "--bootstrap-server localhost:9092 --command-config `"$adminProps`" --list").Output -split "`n" |
        ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notlike '__*' }
    Write-Host "     $(@($topics).Count) topics; ACLs applied (internal topics such as __consumer_offsets not counted)."
}

function Reset-Databases {
    Write-Step 'Dropping the EWP databases and users'
    foreach ($name in $databases) {
        Invoke-Psql 'postgres' "DROP DATABASE IF EXISTS `"$name`" WITH (FORCE);" | Out-Null
        Write-Host "     $name dropped"
    }
    $roles = @(Invoke-Psql 'postgres' "SELECT rolname FROM pg_roles WHERE rolname LIKE 'ewp\_%' ORDER BY 1;" | Where-Object { $_ })
    foreach ($role in $roles) {
        Invoke-Psql 'postgres' "DROP OWNED BY `"$role`"; DROP ROLE `"$role`";" | Out-Null
    }
    Write-Host "     $($roles.Count) ewp_* user(s) dropped."

    Write-Step 'Creating the databases, users, grants, tables and seed data'
    & (Join-Path $PSScriptRoot 'database\Initialize-EwpDatabases.ps1') -Force -PsqlPath $PsqlPath -PostgresPassword $PostgresPassword -HostName $HostName -Port $Port
}

function Clear-DocumentStorage {
    Write-Step "Emptying Documents Management's file store"
    if (Test-Path $storage) {
        $items = @(Get-ChildItem $storage -Force | Where-Object { $_.Name -ne '.gitignore' })
        $items | Remove-Item -Recurse -Force
        Write-Host "     $($items.Count) item(s) removed (.gitignore kept)."
    } else {
        Write-Host '     nothing stored yet.'
    }
}

function Clear-PlaywrightState {
    Write-Step 'Clearing the Playwright sign-ins and results'
    foreach ($folder in @('.auth', 'test-results', 'playwright-report')) {
        $path = Join-Path $playwright $folder
        if (Test-Path $path) { Remove-Item $path -Recurse -Force; Write-Host "     tst\playwright\$folder removed" }
    }
}

# ===================================================================================================
# Which level
# ===================================================================================================
if (-not $Level) {
    Write-Host 'EWP V3 - reset the local platform' -ForegroundColor Cyan
    Write-Host ''
    Write-Host '  [1]  Data only'
    Write-Host '       Databases and their users recreated, stored documents emptied. Kafka untouched.' -ForegroundColor DarkGray
    Write-Host '  [2]  Data + Kafka security'
    Write-Host '       Level 1, plus Kafka users, topics, consumer groups and ACLs recreated / refreshed.' -ForegroundColor DarkGray
    Write-Host '  [3]  Everything (a fresh start)'
    Write-Host '       Level 2, with Kafka wiped and re-formatted first: no message, topic or group left over.' -ForegroundColor DarkGray
    Write-Host ''
    $choice = Read-Host 'Choose 1, 2 or 3 (anything else cancels)'
    if ($choice -notin @('1', '2', '3')) { Write-Host 'Nothing changed.'; exit 1 }
    $Level = [int] $choice
}

$levelName = @{ 1 = 'Data only'; 2 = 'Data + Kafka security'; 3 = 'Everything (a fresh start)' }[$Level]

# ===================================================================================================
# Pre-flight: everything the chosen level needs is where it should be - before anything is stopped.
# ===================================================================================================
if (-not (Test-Path $PsqlPath)) {
    $onPath = Get-Command psql.exe -ErrorAction SilentlyContinue
    if (-not $onPath) { throw "psql.exe not found at '$PsqlPath' or on PATH. Pass -PsqlPath." }
    $PsqlPath = $onPath.Source
}
$env:PGPASSWORD = $PostgresPassword
Invoke-Psql 'postgres' 'SELECT 1;' | Out-Null

if ($Level -ge 2) {
    foreach ($required in @($serverProps, $adminProps)) {
        if (-not (Test-Path $required)) {
            throw "$required not found. Kafka must have been secured once (ps\kafka\Setup-KafkaSecurity.ps1, ReadMe.txt section 5)."
        }
    }
    $script:adminPassword = [regex]::Match((Get-Content $adminProps -Raw), 'password="([^"]+)"').Groups[1].Value
    if (-not $script:adminPassword) { throw "The admin password was not found in $adminProps." }
}
if ($Level -eq 3) {
    $line = Get-Content $serverProps | Where-Object { $_ -match '^\s*log\.dirs\s*=' } | Select-Object -First 1
    if (-not $line) { throw "log.dirs is not set in $serverProps." }
    $script:logDirs = ($line -split '=', 2)[1].Trim().Replace('\\', '\')
}

if (-not $Force) {
    Write-Host "`nLevel $Level - $levelName. This ERASES:" -ForegroundColor Yellow
    Write-Host '  - the running platform is stopped (Visual Studio session, Node.js services, workers, simulators)'
    if ($Level -eq 3) { Write-Host "  - Kafka: everything in $script:logDirs (topics, messages, consumer groups, users, ACLs)" }
    Write-Host '  - PostgreSQL: the 11 EWP databases and every ewp_* user (recreated empty, with seed data)'
    Write-Host "  - the stored documents ($storage)"
    Write-Host '  - the Playwright saved sign-ins and test results'
    if ($Level -ge 2) { Write-Host '  Kafka users, topics, consumer groups and ACLs are then recreated / refreshed.' }
    if ($Level -eq 1) { Write-Host '  Kafka is not touched.' }
    if ((Read-Host 'Type YES to continue') -ne 'YES') { Write-Host 'Nothing changed.'; exit 1 }
}

# ===================================================================================================
# Run the chosen level
# ===================================================================================================
$script:stepCount = @{ 1 = 5; 2 = 7; 3 = 10 }[$Level]
try {
    Stop-Platform
    if ($Level -eq 3) { Reset-KafkaStorage }
    if ($Level -ge 2) { Start-KafkaIfNeeded; Restore-KafkaSecurity }
    Reset-Databases
    Clear-DocumentStorage
    Clear-PlaywrightState
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host "`nThe platform is reset (level $Level - $levelName)." -ForegroundColor Green
Write-Host 'Next: start the solution in Visual Studio, then .\ps\run\Start-NodeServices.ps1.'
Write-Host 'Sign in again in the browser (old sessions are gone); run the Playwright seed scenarios for demo customers.'