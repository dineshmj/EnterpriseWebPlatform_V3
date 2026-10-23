<#
.SYNOPSIS
    Resets or refreshes the local EWP V3 platform, at one of seven levels. No connection strings to
    type: PostgreSQL is reached as the postgres superuser, Kafka as its admin user.
.DESCRIPTION
    Run it with PostgreSQL running. Without -Level it shows a menu:

      1  Data only               the 12 databases and every ewp_* user dropped and recreated (tables,
                                 grants, seed data), the stored documents emptied. Kafka is left as
                                 it is: its messages stay, but every consumer has already read them.
      2  Data + Kafka messages   level 1, plus every Kafka topic emptied (Reset-KafkaRecords): no old
                                 message is left, while the topics, consumer groups, users and ACLs
                                 stay exactly as they are. The quick clean slate.
      3  PostgreSQL users only   the ewp_* users and their grants created or refreshed
                                 (Apply-EwpServiceDbUsers) - e.g. after adding a service user. No data
                                 erased.
      4  Kafka security only     the Kafka users, topics, consumer groups and ACLs created or refreshed
                                 (Setup-KafkaSecurity Prepare + Acls; Kafka started if needed) - e.g.
                                 after adding a topic or a subscriber. No data erased.
      5  PostgreSQL + Kafka      levels 3 and 4 together. No data erased.
      6  Data + Kafka security   levels 1 and 4 together.
      7  Everything              level 6, with Kafka itself wiped and re-formatted first: every topic,
                                 message, consumer group, user and ACL gone, then recreated - nothing
                                 stale, nothing unknown. The full "new laptop" start.

    1 and 3 touch PostgreSQL only and are quick; the others involve Kafka and take longer.

    Every level first checks that no console window of the platform is open (Kafka, the Node.js
    services, the .NET services) and exits if one is. Levels 1, 2, 6 and 7 also delete the
    Playwright saved sign-ins and results.

    After 1, 2, 6 or 7: start the solution in Visual Studio (this also restarts the simulators, whose
    state is in memory), then .\ps\run\Start-NodeServices.ps1, and sign in again (old sessions are gone;
    with Mfa:Enabled on, everybody enrols again). The demo customers come from the Playwright seed
    scenarios.

    DEVELOPMENT ONLY. Levels 1, 2, 6 and 7 ERASE platform data on this machine: they ask for YES (or
    pass -Force).
.EXAMPLE
    .\ps\Reset.ps1                      # menu
.EXAMPLE
    .\ps\Reset.ps1 -Level 1             # data only, asks YES
.EXAMPLE
    .\ps\Reset.ps1 -Level 2             # databases fresh, Kafka topics emptied (kept), asks YES
.EXAMPLE
    .\ps\Reset.ps1 -Level 5             # refresh PostgreSQL users and Kafka security, no data erased
.EXAMPLE
    .\ps\Reset.ps1 -Level 7 -Force      # everything, no questions
#>
param(
    [ValidateSet(1, 2, 3, 4, 5, 6, 7)]
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

$stopwatch = [System.Diagnostics.Stopwatch]::StartNew();

$ErrorActionPreference = 'Stop'
$root = (Resolve-Path (Join-Path $PSScriptRoot '..')).Path   # the repository root (this script is in ps\)

$databases = @(
    'EwpIdentityAccessDb', 'EwpBssShellDb', 'EwpCustomerDb', 'EwpKycDb', 'EwpDocumentsManagementDb',
    'EwpComplianceDb', 'EwpAccountsDb', 'EwpNotificationsDb', 'EwpPaymentsDb', 'EwpAuditDb', 'EwpPartnerProductsDb', 'EwpBffStateDb'
)

# Every port a platform process listens on (ReadMe.txt section 3): IDP, Shell, BFFs, APIs,
# Node.js services, simulators, the workers' health ports and the fault proxy (Toxiproxy: admin
# 8474, routes 47101-47111 - stopping it is what opens every route again). Not Kafka
# (9092/9093) or PostgreSQL.
$platformPorts = @(
    46392, 46367, 46311, 46363, 33800, 46305, 49486, 46399, 46306, 45456, 48486,
    46377, 44488, 46388, 46378, 46379, 46380, 46381, 46382, 46383, 46384, 46385, 46387, 46366, 46376, 46386,
    5101, 5102, 5103, 5104, 5105, 5106, 5107, 5108, 5109,
    8474, 47101, 47102, 47103, 47104, 47105, 47106, 47107, 47108, 47109, 47110, 47111
)

$kafkaWindowTitle = 'Kafka - started by Reset.ps1, keep open'
$serverProps = Join-Path $KafkaHome 'config\server.properties'
$adminProps = Join-Path $KafkaHome 'config\admin.properties'
$storage = Join-Path $root 'src\Microservices\DocumentsManagement\API\DocumentStorage'
$auditAnchors = Join-Path $root 'src\Microservices\Audit\API\AuditAnchors'
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

    # The window gets a title of its own: in the taskbar it groups with the other consoles, and
    # closing it while this script runs would pull Kafka away from under the remaining steps.
    $startCommand = if (Test-Path (Join-Path $KafkaHome 'StartKafka.bat')) { 'StartKafka.bat' }
                    else { 'bin\windows\kafka-server-start.bat config\server.properties' }
    Start-Process -FilePath 'cmd.exe' -WorkingDirectory $KafkaHome `
        -ArgumentList '/c', "title $kafkaWindowTitle && $startCommand"
    Write-Host "     started in its own window (`"$kafkaWindowTitle`"); waiting ..."
    $deadline = (Get-Date).AddSeconds(120)
    while ((Get-Date) -lt $deadline) {
        Start-Sleep -Seconds 3
        if (Test-KafkaAdmin) { Write-Host '     Kafka is running and accepts the admin user.'; return }
    }
    throw 'Kafka did not accept the admin user within 120 s. Look at the Kafka window for the reason.'
}

function Assert-KafkaRunning {
    if (-not (Test-KafkaPort)) {
        throw "Kafka stopped answering on localhost:9092 - was the `"$kafkaWindowTitle`" window closed? Nothing more was done to Kafka; run this script again (same option)."
    }
}

function Restore-KafkaSecurity {
    Write-Step 'Kafka users, topics, consumer groups and ACLs'
    Assert-KafkaRunning
    & (Join-Path $PSScriptRoot 'kafka\Setup-KafkaSecurity.ps1') -Phase Prepare -KafkaHome $KafkaHome
    Assert-KafkaRunning
    & (Join-Path $PSScriptRoot 'kafka\Setup-KafkaSecurity.ps1') -Phase Acls -KafkaHome $KafkaHome | Out-Null
    Assert-KafkaRunning
    $topics = (Invoke-KafkaTool 'kafka-topics.bat' "--bootstrap-server localhost:9092 --command-config `"$adminProps`" --list").Output -split "`n" |
        ForEach-Object { $_.Trim() } | Where-Object { $_ -and $_ -notlike '__*' }
    Write-Host "     $(@($topics).Count) topics; ACLs applied (internal topics such as __consumer_offsets not counted)."
}

function Clear-KafkaMessages {
    Write-Step 'Emptying every Kafka topic (topics, consumer groups, users and ACLs stay)'
    Assert-KafkaRunning
    & (Join-Path $PSScriptRoot 'kafka\Reset-KafkaRecords.ps1') -Force -KafkaHome $KafkaHome
    Assert-KafkaRunning
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

function Update-PostgresUsers {
    Write-Step 'PostgreSQL users and grants (no data erased)'
    & (Join-Path $PSScriptRoot 'database\Apply-EwpServiceDbUsers.ps1') -PsqlPath $PsqlPath -PostgresPassword $PostgresPassword -HostName $HostName -Port $Port
    if ($LASTEXITCODE -ne 0) { throw 'Apply-EwpServiceDbUsers.ps1 failed (a database is missing? Level 1 recreates them).' }
}

function Clear-DocumentStorage {
    # The stored documents, and the audit chain's checkpoints: a recreated audit database starts its
    # chain again at entry 1, so the old checkpoints would report it as truncated.
    Write-Step "Emptying the file stores (documents, audit checkpoints)"
    foreach ($store in @($storage, $auditAnchors)) {
        if (Test-Path $store) {
            $items = @(Get-ChildItem $store -Force | Where-Object { $_.Name -ne '.gitignore' })
            $items | Remove-Item -Recurse -Force
            Write-Host "     $($store.Substring($root.Length + 1)): $($items.Count) item(s) removed (.gitignore kept)."
        } else {
            Write-Host "     $($store.Substring($root.Length + 1)): nothing stored yet."
        }
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
# First: no console window of the platform may be open - the person closes them, so that nothing
# is cut off mid-work:
#   (a) Kafka: the broker (java ... kafka.Kafka) and its StartKafka.bat window;
#   (b) the Node.js services: node.exe from this repository and their runnow.bat windows;
#   (c) the .NET solution: every EnterpriseWebPlatform.* process, and Visual Studio's debug
#       console windows (VsDebugConsole) still open for one of them.
# Anything else still listening on a platform port is reported too. PostgreSQL is not counted.
# ===================================================================================================
$consoles = [System.Collections.Generic.List[object]]::new()
function Add-Console([int] $ProcessId, [string] $What) {
    if ($ProcessId -le 0 -or ($consoles | Where-Object Id -eq $ProcessId)) { return }
    $title = (Get-Process -Id $ProcessId -ErrorAction SilentlyContinue).MainWindowTitle
    $consoles.Add([pscustomobject]@{ Id = $ProcessId; What = $What; Title = $title })
}

$processes = @(Get-CimInstance Win32_Process -ErrorAction SilentlyContinue | Select-Object ProcessId, ParentProcessId, Name, CommandLine)
$rootPattern = [regex]::Escape($root)
# The java launcher starts the broker's JVM as a child with the same command line: one broker, listed once.
$kafkaJava = @($processes | Where-Object { $_.Name -eq 'java.exe' -and "$($_.CommandLine)" -match 'kafka\.Kafka' } |
    Select-Object -ExpandProperty ProcessId)
foreach ($p in $processes) {
    $commandLine = "$($p.CommandLine)"
    switch -Regex ($p.Name) {
        '^java\.exe$'           { if ($p.ProcessId -in $kafkaJava -and $p.ParentProcessId -notin $kafkaJava) { Add-Console $p.ProcessId 'Kafka broker' } }
        '^cmd\.exe$'            {
                                    if ($commandLine -match 'StartKafka\.bat|kafka-server-start') { Add-Console $p.ProcessId 'Kafka console' }
                                    elseif ($commandLine -match 'runnow\.bat') { Add-Console $p.ProcessId 'Node.js console' }
                                }
        '^node\.exe$'           { if ($commandLine -match $rootPattern) { Add-Console $p.ProcessId 'Node.js service' } }
        '^EnterpriseWebPlatform' { Add-Console $p.ProcessId '.NET service' }
        '^toxiproxy-server'     { Add-Console $p.ProcessId 'Fault proxy (Toxiproxy)' }
        '^VsDebugConsole\.exe$' { if ($commandLine -match 'EnterpriseWebPlatform') { Add-Console $p.ProcessId 'Visual Studio debug console' } }
    }
}
foreach ($listener in @(Get-NetTCPConnection -LocalPort $platformPorts -State Listen -ErrorAction SilentlyContinue)) {
    Add-Console $listener.OwningProcess "listening on platform port $($listener.LocalPort)"
}

if ($consoles.Count -gt 0) {
    Write-Host 'The platform is still running. Close these console windows first, then run this script again:' -ForegroundColor Yellow
    foreach ($console in $consoles | Sort-Object What, Id) {
        $name = (Get-Process -Id $console.Id -ErrorAction SilentlyContinue).ProcessName
        $title = if ($console.Title) { " - `"$($console.Title)`"" } else { '' }
        Write-Host ("  - {0,-28} {1} (PID {2}){3}" -f $console.What, $name, $console.Id, $title)
    }
    Write-Host ''
    Write-Host 'Tip: stop debugging in Visual Studio (Shift+F5), run .\ps\run\Start-NodeServices.ps1 -Stop and .\ps\toxiproxy\Start-Toxiproxy.ps1 -Stop,'
    Write-Host '     and close the Kafka window (Ctrl+C, then Y). Options 2 and 4 to 7 start Kafka again themselves.'
    Write-Host 'Nothing changed.'
    exit 1
}

# ===================================================================================================
# Which level
# ===================================================================================================
# A plain hashtable: an [ordered] one reads an integer index as a POSITION ($names[1] = the second entry).
$levelNames = @{
    1 = 'Data only'
    2 = 'Data + Kafka messages cleared'
    3 = 'PostgreSQL users only'
    4 = 'Kafka security only'
    5 = 'PostgreSQL users + Kafka security'
    6 = 'Data + Kafka security'
    7 = 'Everything (a fresh start)'
}

if (-not $Level) {
    $hints = @{
        1 = 'Databases and their users recreated, stored documents emptied. Kafka untouched.'
        2 = 'Level 1, plus every Kafka topic emptied; topics, consumer groups, users and ACLs stay.'
        3 = 'PostgreSQL ewp_* users and their grants created / refreshed. No data erased.'
        4 = 'Kafka users, topics, consumer groups and ACLs created / refreshed. No data erased.'
        5 = 'Levels 3 and 4 together. No data erased.'
        6 = 'Levels 1 and 4 together.'
        7 = 'Level 6, with Kafka wiped and re-formatted first: no message, topic or group left over.'
    }
    Write-Host 'EWP V3 - reset the local platform' -ForegroundColor Cyan
    Write-Host ''
    foreach ($key in 1..7) {
        Write-Host "  [$key]  $($levelNames[$key])"
        Write-Host "       $($hints[$key])" -ForegroundColor DarkGray
    }
    Write-Host ''
    $choice = Read-Host 'Choose 1 to 7 (anything else cancels)'
    if ($choice -notin @('1', '2', '3', '4', '5', '6', '7')) { Write-Host 'Nothing changed.'; exit 1 }
    $Level = [int] $choice
}

$levelName = $levelNames[$Level]

Clear-Host
Write-Host "EWP V3 - reset the local platform: option $Level - $levelName" -ForegroundColor Cyan

# What the chosen level does.
$erasesData = $Level -in 1, 2, 6, 7    # stop the platform, recreate the databases, empty documents and Playwright state
$clearsKafkaMessages = $Level -eq 2    # every topic emptied; topics, groups, users and ACLs kept
$postgresUsers = $Level -in 3, 5       # ewp_* users and grants refreshed in place (levels 1/2/6/7 recreate them anyway)
$kafkaSecurity = $Level -in 4, 5, 6, 7 # Kafka users, topics, groups and ACLs
$wipesKafka = $Level -eq 7             # Kafka wiped and re-formatted first

# ===================================================================================================
# Pre-flight: everything the chosen level needs is where it should be - before anything is stopped.
# ===================================================================================================
if ($erasesData -or $postgresUsers) {
    if (-not (Test-Path $PsqlPath)) {
        $onPath = Get-Command psql.exe -ErrorAction SilentlyContinue
        if (-not $onPath) { throw "psql.exe not found at '$PsqlPath' or on PATH. Pass -PsqlPath." }
        $PsqlPath = $onPath.Source
    }
    $env:PGPASSWORD = $PostgresPassword
    Invoke-Psql 'postgres' 'SELECT 1;' | Out-Null
}

if ($kafkaSecurity -or $clearsKafkaMessages) {
    foreach ($required in @($serverProps, $adminProps)) {
        if (-not (Test-Path $required)) {
            throw "$required not found. Kafka must have been secured once (ps\kafka\Setup-KafkaSecurity.ps1, ReadMe.txt section 5)."
        }
    }
    $script:adminPassword = [regex]::Match((Get-Content $adminProps -Raw), 'password="([^"]+)"').Groups[1].Value
    if (-not $script:adminPassword) { throw "The admin password was not found in $adminProps." }
}
if ($wipesKafka) {
    $line = Get-Content $serverProps | Where-Object { $_ -match '^\s*log\.dirs\s*=' } | Select-Object -First 1
    if (-not $line) { throw "log.dirs is not set in $serverProps." }
    $script:logDirs = ($line -split '=', 2)[1].Trim().Replace('\\', '\')
}

# Only the levels that erase data ask: refreshing users and permissions is safe to re-run.
if ($erasesData -and -not $Force) {
    Write-Host "`nLevel $Level - $levelName. This ERASES:" -ForegroundColor Yellow
    Write-Host '  - the running platform is stopped (Visual Studio session, Node.js services, workers, simulators)'
    if ($wipesKafka) { Write-Host "  - Kafka: everything in $script:logDirs (topics, messages, consumer groups, users, ACLs)" }
    if ($clearsKafkaMessages) { Write-Host '  - Kafka: every message on every topic (the topics, consumer groups, users and ACLs stay)' }
    Write-Host '  - PostgreSQL: the 12 EWP databases and every ewp_* user (recreated empty, with seed data)'
    Write-Host "  - the stored documents ($storage)"
    Write-Host "  - the audit chain's checkpoints ($auditAnchors)"
    Write-Host '  - the Playwright saved sign-ins and test results'
    if ($kafkaSecurity) { Write-Host '  Kafka users, topics, consumer groups and ACLs are then recreated / refreshed.' }
    elseif (-not $clearsKafkaMessages) { Write-Host '  Kafka is not touched.' }
    if ((Read-Host 'Type YES to continue') -ne 'YES') { Write-Host 'Nothing changed.'; exit 1 }
}

# ===================================================================================================
# Run the chosen level
# ===================================================================================================
$script:stepCount = 0
if ($erasesData) { $script:stepCount += 5 }      # stop, drop, create, documents, Playwright
if ($wipesKafka) { $script:stepCount += 3 }      # stop Kafka, wipe, format
if ($kafkaSecurity) { $script:stepCount += 2 }   # Kafka running, security
if ($clearsKafkaMessages) { $script:stepCount += 2 }   # Kafka running, topics emptied
if ($postgresUsers) { $script:stepCount += 1 }
try {
    if ($erasesData) { Stop-Platform }
    if ($wipesKafka) { Reset-KafkaStorage }
    if ($postgresUsers) { Update-PostgresUsers }
    if ($kafkaSecurity) { Start-KafkaIfNeeded; Restore-KafkaSecurity }
    if ($clearsKafkaMessages) { Start-KafkaIfNeeded; Clear-KafkaMessages }
    if ($erasesData) {
        Reset-Databases
        Clear-DocumentStorage
        Clear-PlaywrightState
    }
}
finally {
    Remove-Item Env:PGPASSWORD -ErrorAction SilentlyContinue
}

Write-Host "`nDone (level $Level - $levelName)." -ForegroundColor Green
if ($erasesData) {
    Write-Host 'Next: start the solution in Visual Studio, then .\ps\run\Start-NodeServices.ps1.'
    Write-Host 'Sign in again in the browser (old sessions are gone); run the Playwright seed scenarios for demo customers.'
} else {
    Write-Host 'Nothing was erased; running services pick the changes up on their next connection.'
}

$stopwatch.Stop()

Write-Host "`r`nResetting BSS finished in: $($stopwatch.Elapsed.ToString("m' minutes 's' seconds'"))" -ForegroundColor Green