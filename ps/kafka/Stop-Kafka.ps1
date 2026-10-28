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

# ┌─ NOTE ───────────────────────────────────────────────────────────────────────────────
# │ Windows does not always show a process's command line (then no broker is found by it
# │ above), and the broker's java.exe can itself be started by another java.exe. So also
# │ take every java.exe above a port owner: stopping only the child can leave its parent
# │ running and the ports in use.
# └──────────────────────────────────────────────────────────────────────────────────────
$javaParents = foreach ($ownerId in $portOwnerIds) {
    $current = Get-CimInstance Win32_Process -Filter "ProcessId=$ownerId" -ErrorAction SilentlyContinue
    while ($current) {
        $parent = Get-CimInstance Win32_Process -Filter "ProcessId=$($current.ParentProcessId)" -ErrorAction SilentlyContinue
        if (-not $parent -or $parent.Name -ne 'java.exe') { break }
        $parent.ProcessId
        $current = $parent
    }
}

$ids = @($brokerIds + $javaParents + $portOwnerIds | Where-Object { $_ -and $_ -ne 0 } | Sort-Object -Unique)

if ($ids.Count -eq 0) {
    Write-Host 'Kafka is not running.' -ForegroundColor Green
}
else {
    foreach ($id in $ids) {
        $p = Get-Process -Id $id -ErrorAction SilentlyContinue
        if ($p) {
            Write-Host "Stopping $($p.ProcessName) (PID $id, started $($p.StartTime))"
            # ┌─ NOTE ───────────────────────────────────────────────────────────────────────
            # │ taskkill, not Stop-Process: Windows PowerShell's Stop-Process also asks to read
            # │ the process, which the broker's java.exe can refuse ("Access is denied") while
            # │ still allowing it to be terminated - taskkill asks for terminate only, and /T
            # │ ends the processes it started too. The list is taken up front, and stopping one
            # │ process can end another on it: one that is already gone is what we wanted.
            # └──────────────────────────────────────────────────────────────────────────────
            # Through cmd.exe: Windows PowerShell 5.1 turns a native program's error text into a
            # terminating error under $ErrorActionPreference = 'Stop' - even "not found", which is fine here.
            $output = cmd.exe /c "taskkill.exe /PID $id /T /F 2>&1"
            if ($LASTEXITCODE -ne 0) {
                if (Get-Process -Id $id -ErrorAction SilentlyContinue) {
                    throw "Cannot stop PID $id ($($p.ProcessName)): $output Stop Kafka from an administrator window, or start it again from a normal one."
                }
                Write-Host "  (PID $id had already ended)" -ForegroundColor DarkGray
            }
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

# Stopped: success, whatever taskkill answered for a process that had already ended (128).
exit 0