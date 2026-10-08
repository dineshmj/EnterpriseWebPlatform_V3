<#
.SYNOPSIS
    Empties every EWP Kafka topic - all records are deleted - while the topics, their ACLs, their
    settings and the consumer groups stay as they are. For starting again from a clean slate.

.DESCRIPTION
    Recreating the databases does not touch Kafka: the old events stay on the topics, and a new
    consumer (e.g. the Audit API's trail, which starts at the earliest offset) would record a week
    of history about customers that no longer exist. This deletes, on every topic, every record up
    to its current end (kafka-delete-records with offset -1 = the high watermark). Offsets are not
    reset to 0: each topic simply continues from where it ended, and every consumer group, already
    at that end, has nothing old to replay.

    A clean slate, in this order (everything else stopped; Kafka and PostgreSQL running):
        .\ps\kafka\Reset-KafkaRecords.ps1
        .\ps\database\Initialize-EwpDatabases.ps1
    then start Visual Studio and .\ps\run\Start-NodeServices.ps1.

    DEVELOPMENT ONLY. It asks first (or pass -Force).

.EXAMPLE
    .\ps\kafka\Reset-KafkaRecords.ps1
#>
param(
    [switch] $Force,
    [string] $KafkaHome = 'C:\Kafka'
)

$ErrorActionPreference = 'Stop'

$Bootstrap  = 'localhost:9092'
$Bin        = Join-Path $KafkaHome 'bin\windows'
$AdminProps = Join-Path $KafkaHome 'config\admin.properties'

# The Kafka tools run from an empty folder (see Setup-KafkaSecurity.ps1: a "*" argument would
# otherwise be expanded by the Java launcher).
$ToolWorkingDir = Join-Path $env:TEMP 'ewp-kafka-tools'
New-Item -ItemType Directory -Force $ToolWorkingDir | Out-Null

function Invoke-KafkaTool([string] $Tool, [string] $Arguments) {
    $exe = Join-Path $Bin $Tool
    $output = cmd.exe /d /c "cd /d `"$ToolWorkingDir`" && `"$exe`" $Arguments 2>&1"
    if ($LASTEXITCODE -ne 0) { throw "$Tool failed (exit $LASTEXITCODE):`n$($output -join "`n")" }
    return $output
}

$client = New-Object System.Net.Sockets.TcpClient
try { $client.Connect('localhost', 9092) } catch { throw 'Kafka is not running on localhost:9092. Start it (C:\Kafka\StartKafka.bat) and re-run.' } finally { $client.Dispose() }
if (-not (Test-Path $AdminProps)) { throw "$AdminProps is missing: Kafka is not secured yet (ps\kafka\Setup-KafkaSecurity.ps1)." }
$auth = "--bootstrap-server $Bootstrap --command-config `"$AdminProps`""

# Every topic and its partitions (internal topics such as __consumer_offsets excluded).
$topics = Invoke-KafkaTool 'kafka-topics.bat' "$auth --list" |
    Where-Object { $_ -and -not $_.StartsWith('__') -and $_ -notmatch '\s' }
if (-not $topics) { Write-Host 'No topics found.'; return }

$partitions = foreach ($topic in $topics) {
    $describe = Invoke-KafkaTool 'kafka-topics.bat' "$auth --describe --topic $topic"
    foreach ($line in $describe) {
        if ($line -match "Topic:\s*$([regex]::Escape($topic))\s+.*Partition:\s*(\d+)") {
            [ordered]@{ topic = $topic; partition = [int]$Matches[1]; offset = -1 }
        }
    }
}

Write-Host "Topics: $($topics.Count), partitions: $(@($partitions).Count)." -ForegroundColor Cyan
if (-not $Force) {
    Write-Host 'This DELETES every record on every topic above (the topics, ACLs and consumer groups stay).' -ForegroundColor Yellow
    Write-Host 'Stop every EWP service first (Visual Studio, and .\ps\run\Start-NodeServices.ps1 -Stop).' -ForegroundColor Yellow
    if ((Read-Host 'Type YES to continue') -ne 'YES') { Write-Host 'Nothing changed.'; exit 1 }
}

# offset -1 = up to the high watermark: everything currently on the partition.
$jsonFile = Join-Path $ToolWorkingDir 'delete-records.json'
@{ version = 1; partitions = @($partitions) } | ConvertTo-Json -Depth 4 | Set-Content -Path $jsonFile -Encoding ASCII
Invoke-KafkaTool 'kafka-delete-records.bat' "$auth --offset-json-file `"$jsonFile`"" | Out-Null
Remove-Item $jsonFile -Force

Write-Host "Done: every topic is empty. Now recreate the databases: .\ps\database\Initialize-EwpDatabases.ps1" -ForegroundColor Green