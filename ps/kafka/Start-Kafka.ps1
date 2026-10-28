<#
.SYNOPSIS
    Starts the local Kafka broker (C:\Kafka) in a console window of its own.

.DESCRIPTION
    Opens Kafka (StartKafka.bat, or kafka-server-start.bat with config\server.properties when
    there is no StartKafka.bat) in a classic console window (conhost.exe) - never inside the
    PowerShell ISE or a Windows Terminal tab that runs this script - so Ctrl+C in that window
    stops Kafka cleanly. Then waits until the broker answers on localhost:9092.

    Does nothing when Kafka already answers. Before starting, clears the read-only flag on the
    metadata snapshot files, which otherwise makes the start fail on Windows after a hard stop
    (AccessDeniedException on *.checkpoint.deleted).

    Stop it with Ctrl+C (then Y) in its window, or with .\ps\kafka\Stop-Kafka.ps1.

.EXAMPLE
    .\ps\kafka\Start-Kafka.ps1
#>
[CmdletBinding()]
param(
    [string] $KafkaHome = 'C:\Kafka',
    [int] $Port = 9092,
    [int] $TimeoutSeconds = 120
)

$ErrorActionPreference = 'Stop'

$windowTitle = 'Kafka - started by Start-Kafka.ps1, keep open'

cls;

function Test-KafkaPort {
    $client = New-Object System.Net.Sockets.TcpClient
    try { $client.Connect('localhost', $Port); return $true } catch { return $false } finally { $client.Dispose() }
}

if (Test-KafkaPort) {
    Write-Host "Kafka is already running on localhost:$Port." -ForegroundColor Green
    return
}

if (-not (Test-Path (Join-Path $KafkaHome 'config\server.properties'))) {
    throw "No Kafka installation at $KafkaHome (config\server.properties not found)."
}

# Windows: Kafka marks metadata snapshots read-only, and a read-only file cannot be
# deleted, which makes the start fail. Clearing the flag changes no content.
$metadata = Join-Path $KafkaHome 'Logs\__cluster_metadata-0'
if (Test-Path $metadata) {
    $readOnly = @(Get-ChildItem $metadata -Force -File |
        Where-Object { $_.Name -like '*.checkpoint*' -and ($_.Attributes -band [IO.FileAttributes]::ReadOnly) })
    foreach ($f in $readOnly) { $f.Attributes = $f.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) }
    if ($readOnly.Count -gt 0) { Write-Host "Cleared the read-only flag on $($readOnly.Count) metadata snapshot file(s)." }
}

# ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
# │ ".\" on purpose: with NoDefaultCurrentDirectoryInExePath set (some tools set it, a
# │ guard against planted programs), cmd.exe does not look in the current folder for a
# │ bare "StartKafka.bat" - Kafka would silently never start.
# └──────────────────────────────────────────────────────────────────────────────────────
$startCommand = if (Test-Path (Join-Path $KafkaHome 'StartKafka.bat')) { '.\StartKafka.bat' }
                else { '.\bin\windows\kafka-server-start.bat config\server.properties' }

# conhost.exe: a classic console window of its own, whichever host runs this script.
Start-Process -FilePath (Join-Path $env:SystemRoot 'System32\conhost.exe') -WorkingDirectory $KafkaHome `
    -WindowStyle Normal -ArgumentList "cmd.exe /c `"title $windowTitle && $startCommand`""
Write-Host "Kafka started in its own window (`"$windowTitle`"); waiting for localhost:$Port ..."

$deadline = (Get-Date).AddSeconds($TimeoutSeconds)
while ((Get-Date) -lt $deadline) {
    Start-Sleep -Seconds 2
    if (Test-KafkaPort) {
        Write-Host "Kafka is running on localhost:$Port. Keep its window open; Ctrl+C there stops it." -ForegroundColor Green
        return
    }
}
throw "Kafka did not answer on localhost:$Port within $TimeoutSeconds s. Look at the Kafka window for the reason."