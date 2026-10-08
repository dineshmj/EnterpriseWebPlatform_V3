<#
.SYNOPSIS
    Stops the local Kafka broker (C:\Kafka) without needing its process ID.

.DESCRIPTION
    Finds every java.exe whose command line runs kafka.Kafka, plus any process that
    listens on the broker ports (9092 / 9093), and stops them. Then waits until the
    ports are free and clears the read-only flag on the metadata snapshot files, which
    otherwise make the next start fail on Windows (AccessDeniedException on
    *.checkpoint.deleted).

    Prefer Ctrl+C in Kafka's own console window when you can: that is a clean shutdown.
    This script is a hard stop, for when Ctrl+C is not available (e.g. Kafka started from
    PowerShell ISE, or its window was closed). Kafka recovers from a hard stop on its next start.

.EXAMPLE
    .\ps\kafka\Stop-Kafka.ps1
#>
[CmdletBinding()]
param(
    [string] $KafkaHome = 'C:\Kafka',
    [int[]] $Ports = @(9092, 9093)
)

$ErrorActionPreference = 'Stop'

$brokerIds = @(Get-CimInstance Win32_Process -Filter "Name='java.exe'" |
    Where-Object { $_.CommandLine -match 'kafka\.Kafka' } |
    Select-Object -ExpandProperty ProcessId)

$portOwnerIds = @(Get-NetTCPConnection -LocalPort $Ports -State Listen -ErrorAction SilentlyContinue |
    Select-Object -ExpandProperty OwningProcess)

$ids = @($brokerIds + $portOwnerIds | Where-Object { $_ -and $_ -ne 0 } | Sort-Object -Unique)

if ($ids.Count -eq 0) {
    Write-Host 'Kafka is not running.' -ForegroundColor Green
}
else {
    foreach ($id in $ids) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p) {
            Write-Host "Stopping $($p.ProcessName) (PID $id, started $($p.StartTime))"
            Stop-Process -Id $id -Force
        }
    }

    $deadline = (Get-Date).AddSeconds(30)
    while ((Get-NetTCPConnection -LocalPort $Ports -State Listen -ErrorAction SilentlyContinue) -and (Get-Date) -lt $deadline) {
        Start-Sleep -Milliseconds 500
    }
    if (Get-NetTCPConnection -LocalPort $Ports -State Listen -ErrorAction SilentlyContinue) {
        throw "Ports $($Ports -join ', ') are still in use after 30 s."
    }
    Write-Host 'Kafka stopped; ports are free.' -ForegroundColor Green
}

# Windows: Kafka marks metadata snapshots read-only, and a read-only file cannot be
# deleted, which makes the next start fail. Clearing the flag changes no content.
$metadata = Join-Path $KafkaHome 'Logs\__cluster_metadata-0'
if (Test-Path $metadata) {
    $readOnly = @(Get-ChildItem $metadata -Force -File |
        Where-Object { $_.Name -like '*.checkpoint*' -and ($_.Attributes -band [IO.FileAttributes]::ReadOnly) })
    foreach ($f in $readOnly) { $f.Attributes = $f.Attributes -band (-bnot [IO.FileAttributes]::ReadOnly) }
    if ($readOnly.Count -gt 0) { Write-Host "Cleared the read-only flag on $($readOnly.Count) metadata snapshot file(s)." }
}